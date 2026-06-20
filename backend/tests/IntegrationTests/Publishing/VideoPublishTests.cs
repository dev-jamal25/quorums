using System.Text.Json;
using Backend.Core.Common;
using Backend.Core.Domain;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Backend.Core.Storage;
using Backend.Infrastructure.Integrations.Meta;
using Backend.Infrastructure.Jobs;
using Backend.IntegrationTests.Durability;
using Backend.IntegrationTests.Support;
using Xunit;

namespace Backend.IntegrationTests.Publishing;

/// <summary>
/// DL-058 Slice-A: the publish half of the mock end-to-end proof — an assembled <b>video</b> draft at the
/// gate is approved and resumes through the real <c>ResumeRun → PublishingExecutor → PublishCoordinator</c>
/// over RLS Postgres to a <b>mock</b> publish (<c>Meta:Mode=mock</c>), reaching <c>Done</c> with a stable
/// <c>mock://meta/</c> external ref. This does not touch the live publish path (Slice B owns IG Reel / FB
/// video); it proves a video content item flows the same gate → mock-publish seam an image already does.
/// </summary>
[Trait("Category", "Publish")]
[Collection("Durability")]
public sealed class VideoPublishTests
{
    private static readonly string[] _hashtags = ["#reel"];

    private readonly DurabilityFixture _fixture;

    public VideoPublishTests(DurabilityFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Approved_video_run_reaches_mock_published_done()
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA, RunStatus.AwaitingApproval);

        var assetId = DeterministicGuid.From(runId, "asset");
        var mediaRef = new MediaAssetRef(
            assetId, StorageKeys.ForAsset(_fixture.BrandA, assetId, "mp4"), "video", "video/mp4", DurationSec: 5);
        var caption = new Caption("Reel hook", "Reel body", _hashtags, new Grounding(false, [], Confidence.Low));
        var draft = new ContentItemDraft(caption, mediaRef, _fixture.BrandA, Status: "pending");
        var state = TestGeneration.VideoSeed(runId, _fixture.BrandA) with
        {
            Phase = GraphPhase.AwaitingApproval,
            Caption = caption,
            Media = mediaRef,
            Draft = draft,
        };
        await _fixture.SeedCheckpointAsync(
            runId, _fixture.BrandA, JsonSerializer.Serialize(state, RunStateJsonOptions.Options));

        // Human gate: approve (AwaitingApproval → Publishing + an ApprovalAction), then resume → mock publish.
        await _fixture.ApproveRunAsync(runId, _fixture.BrandA);

        var mock = new MockMetaIntegration();
        var (db, job) = _fixture.CreateResumeRunJob(_fixture.BrandA, mock);
        await using (db)
        {
            await job.ExecuteAsync(runId, _fixture.BrandA);
        }

        Assert.Equal(RunStatus.Done, await _fixture.ReadRunStatusAsync(runId, _fixture.BrandA));
        Assert.Equal(1, mock.PublishedMediaCount);

        var final = await _fixture.ReadCheckpointStateAsync(runId, _fixture.BrandA);
        Assert.Equal(GraphPhase.Done, final!.Phase);
        Assert.Equal(PublishStatus.Published, final.Publish!.Status);
        Assert.StartsWith("mock://meta/", final.Publish.ExternalRef!);
        Assert.Equal("video/mp4", final.Draft!.MediaRef!.MimeType); // the published item was the video
    }
}
