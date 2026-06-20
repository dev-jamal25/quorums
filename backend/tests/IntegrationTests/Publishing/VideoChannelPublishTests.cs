using System.Text.Json;
using Backend.Core.Common;
using Backend.Core.Domain;
using Backend.Core.Integrations;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Backend.Core.Storage;
using Backend.Infrastructure.Integrations.Meta;
using Backend.Infrastructure.Jobs;
using Backend.IntegrationTests.Durability;
using Backend.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Backend.IntegrationTests.Publishing;

/// <summary>
/// DL-058 Slice B (mock proofs): the live video branch publishes a reel (IG) + a page video (FB) through
/// the SAME channel-aware two-step + <c>(contentItemId, channel)</c> idempotency as image (DL-055). The
/// mock models REAL Meta on the video surface (re-publish does NOT dedup), so the per-channel double is
/// prevented by the coordinator's GUARD, not a pretend dedup. Also: a reel whose container never reaches
/// FINISHED is bounded and surfaces a <c>ToolError</c> (no hang, no graph exception, DL-022).
/// </summary>
[Trait("Category", "Publish")]
[Collection("Durability")]
public sealed class VideoChannelPublishTests
{
    private static readonly string[] _hashtags = ["#reel"];

    private readonly DurabilityFixture _fixture;

    public VideoChannelPublishTests(DurabilityFixture fixture) => _fixture = fixture;

    // A video publish request (reel surface, mp4 url) — the live path maps this to IG REELS / FB /videos.
    private static PublishRequest VideoRequest(Guid contentItemId, PublishChannel channel) =>
        new(contentItemId, channel, "placeholder", PostSurface.Reel, "brands/a/assets/x.mp4", "hook\n\nbody", _hashtags, AccessToken: "token");

    private async Task<PublishResult> PublishAsync(MockMetaIntegration mock, Guid contentItemId, Guid runId, PublishChannel channel)
    {
        var (db, scope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (db)
        {
            var coordinator = new PublishCoordinator(db, scope, mock);
            return await coordinator.PublishAsync(VideoRequest(contentItemId, channel), runId, _fixture.BrandA);
        }
    }

    private async Task<PublishRecord?> ReadRecordAsync(Guid contentItemId, PublishChannel channel)
    {
        var (db, scope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();
            var record = await db.PublishRecords.AsNoTracking()
                .FirstOrDefaultAsync(r => r.ContentItemId == contentItemId && r.Channel == channel);
            await handle.CompleteAsync();
            return record;
        }
    }

    [Fact]
    public async Task Video_republish_is_prevented_by_the_guard_per_channel_not_by_a_pretend_dedup()
    {
        var contentItemId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var mock = new MockMetaIntegration();

        // Two CLEAN publish attempts per channel — a Hangfire retry of an already-finalized publish. The
        // first finalizes the (contentItemId, channel) PublishRecord; the second is short-circuited by the
        // guard (finalized record → return existing), so Meta is NEVER called a second time. The mock does
        // NOT dedup video, so a missing guard would double-post.
        var ig1 = await PublishAsync(mock, contentItemId, runId, PublishChannel.Instagram);
        var ig2 = await PublishAsync(mock, contentItemId, runId, PublishChannel.Instagram);
        var fb1 = await PublishAsync(mock, contentItemId, runId, PublishChannel.FacebookPage);
        var fb2 = await PublishAsync(mock, contentItemId, runId, PublishChannel.FacebookPage);

        // Every attempt reports Published; the retry returns the SAME finalized ExternalRef per channel.
        Assert.All([ig1, ig2, fb1, fb2], r => Assert.Equal(PublishStatus.Published, r.Status));
        Assert.Equal(ig1.ExternalRef, ig2.ExternalRef);
        Assert.Equal(fb1.ExternalRef, fb2.ExternalRef);
        Assert.StartsWith("mock://meta/Instagram/", ig1.ExternalRef!);
        Assert.StartsWith("mock://meta/FacebookPage/", fb1.ExternalRef!);
        Assert.NotEqual(ig1.ExternalRef, fb1.ExternalRef);

        // EXACTLY one post per channel — the guard prevented the double.
        Assert.Equal(1, mock.PublishedMediaCountFor(PublishChannel.Instagram));
        Assert.Equal(1, mock.PublishedMediaCountFor(PublishChannel.FacebookPage));

        // Proof the GUARD (not a fake dedup) did it: Meta publish ran once per channel (2 total), not 4 —
        // the second attempt per channel never reached Meta. (The mock would double-post if it had.)
        Assert.Equal(2, mock.PublishAttemptCount);

        // One finalized record per channel, distinct refs.
        var igRecord = await ReadRecordAsync(contentItemId, PublishChannel.Instagram);
        var fbRecord = await ReadRecordAsync(contentItemId, PublishChannel.FacebookPage);
        Assert.Equal(ig1.ExternalRef, igRecord!.ExternalRef);
        Assert.Equal(fb1.ExternalRef, fbRecord!.ExternalRef);
    }

    [Fact]
    public async Task Reel_container_that_never_finishes_is_bounded_and_surfaces_a_toolerror()
    {
        var runId = await SeedPublishableVideoRunAsync();
        // The reel container is perpetually "processing" → every poll is a transient failure; the bounded
        // Hangfire retry budget exhausts and the run ends Failed with a ToolError — never a hang or throw.
        var mock = new MockMetaIntegration { FailPollWith = PublishStatus.TransientFailure };

        var (db, job) = _fixture.CreateResumeRunJob(_fixture.BrandA, mock);
        await using (db)
        {
            await Assert.ThrowsAsync<TransientPublishException>(() => job.ExecuteAsync(runId, _fixture.BrandA, 0));
            await Assert.ThrowsAsync<TransientPublishException>(() => job.ExecuteAsync(runId, _fixture.BrandA, 1));
            await Assert.ThrowsAsync<TransientPublishException>(() => job.ExecuteAsync(runId, _fixture.BrandA, 2));
            await job.ExecuteAsync(runId, _fixture.BrandA, 3); // final allotted attempt → Failed, no throw
        }

        Assert.Equal(RunStatus.Failed, await _fixture.ReadRunStatusAsync(runId, _fixture.BrandA));
        Assert.Equal(0, mock.PublishedMediaCount); // poll never finished → nothing published

        var state = await _fixture.ReadCheckpointStateAsync(runId, _fixture.BrandA);
        Assert.Contains(state!.Errors, e => e.Code == "meta.publish_failed");
    }

    // Seeds a publishable VIDEO run (reel surface, video draft) at Publishing status — the post-approval
    // state ResumeRun resumes from, mirroring PublishNodeTests' image seed.
    private async Task<Guid> SeedPublishableVideoRunAsync()
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA, RunStatus.Publishing);
        var assetId = DeterministicGuid.From(runId, "asset");
        var mediaRef = new MediaAssetRef(
            assetId, StorageKeys.ForAsset(_fixture.BrandA, assetId, "mp4"), "video", "video/mp4", DurationSec: 5);
        var caption = new Caption("Reel hook", "Reel body", _hashtags, new Grounding(false, [], Confidence.Low));
        var draft = new ContentItemDraft(caption, mediaRef, _fixture.BrandA, Status: "approved");
        var state = TestGeneration.VideoSeed(runId, _fixture.BrandA) with
        {
            Phase = GraphPhase.AwaitingApproval,
            Caption = caption,
            Media = mediaRef,
            Draft = draft,
        };
        await _fixture.SeedCheckpointAsync(
            runId, _fixture.BrandA, JsonSerializer.Serialize(state, RunStateJsonOptions.Options));
        return runId;
    }
}
