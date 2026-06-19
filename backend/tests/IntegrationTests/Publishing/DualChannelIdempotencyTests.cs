using Backend.Core.Domain;
using Backend.Core.Integrations;
using Backend.Core.Orchestration.Contracts;
using Backend.Infrastructure.Integrations.Meta;
using Backend.IntegrationTests.Durability;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Backend.IntegrationTests.Publishing;

/// <summary>
/// The DL-055 acceptance gate: one approved content item published to BOTH Instagram and Facebook Page,
/// each as its own <c>(contentItemId, channel)</c> idempotency unit. Drives the channel-aware
/// <see cref="PublishCoordinator"/> per channel against the dual-channel <see cref="MockMetaIntegration"/>
/// over a real RLS-bound Postgres (the durability fixture). Crashing one channel in either durability
/// window and retrying must leave EXACTLY one finalized record per channel (distinct ExternalRefs), one
/// effective publish on the crashed channel (re-publish deduped, no second post), and the other channel
/// wholly unaffected. This fails by construction if the key is shared across channels, the mock fakes a
/// single channel, or the FB path lacks its own two-step + crash windows — the point of the slice.
/// </summary>
[Trait("Category", "Publish")]
[Collection("Durability")]
public sealed class DualChannelIdempotencyTests
{
    private readonly DurabilityFixture _fixture;

    public DualChannelIdempotencyTests(DurabilityFixture fixture) => _fixture = fixture;

    private static PublishRequest Request(Guid contentItemId, PublishChannel channel) =>
        new(contentItemId, channel, "placeholder", PostSurface.FeedImage, "brands/a/assets/x.png", "hook\n\nbody", ["#a"], AccessToken: "token");

    // Each publish runs in its OWN job/context (a new coordinator per attempt), exactly as a Hangfire
    // segment would; the mock (Meta's server-side state) and Postgres (the PublishRecord) persist across.
    private async Task<PublishResult> PublishAsync(MockMetaIntegration mock, Guid contentItemId, Guid runId, PublishChannel channel)
    {
        var (db, scope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (db)
        {
            var coordinator = new PublishCoordinator(db, scope, mock);
            return await coordinator.PublishAsync(Request(contentItemId, channel), runId, _fixture.BrandA);
        }
    }

    private async Task PublishExpectingCrashAsync(MockMetaIntegration mock, Guid contentItemId, Guid runId, PublishChannel channel)
    {
        var (db, scope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (db)
        {
            var coordinator = new PublishCoordinator(db, scope, mock);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.PublishAsync(Request(contentItemId, channel), runId, _fixture.BrandA));
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
    public async Task Facebook_crash_in_create_window_recovers_and_instagram_is_unaffected()
    {
        var contentItemId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var mock = new MockMetaIntegration { CrashAfterCreateOnChannel = PublishChannel.FacebookPage };

        // Instagram publishes cleanly on the first pass — an independent (contentItemId, channel) unit.
        var ig = await PublishAsync(mock, contentItemId, runId, PublishChannel.Instagram);

        // Variant A: Facebook crashes in the create→commit-CreationId window; the retry recovers.
        await PublishExpectingCrashAsync(mock, contentItemId, runId, PublishChannel.FacebookPage);
        var fb = await PublishAsync(mock, contentItemId, runId, PublishChannel.FacebookPage);

        // 1) Exactly one finalized record per channel, each with a distinct ExternalRef.
        Assert.Equal(PublishStatus.Published, ig.Status);
        Assert.Equal(PublishStatus.Published, fb.Status);
        Assert.StartsWith("mock://meta/Instagram/", ig.ExternalRef!);
        Assert.StartsWith("mock://meta/FacebookPage/", fb.ExternalRef!);
        Assert.NotEqual(ig.ExternalRef, fb.ExternalRef);

        var igRecord = await ReadRecordAsync(contentItemId, PublishChannel.Instagram);
        var fbRecord = await ReadRecordAsync(contentItemId, PublishChannel.FacebookPage);
        Assert.Equal(ig.ExternalRef, igRecord!.ExternalRef);
        Assert.Equal(fb.ExternalRef, fbRecord!.ExternalRef);

        // 2) Facebook performed exactly one effective publish; the crashed create left one orphan photo.
        Assert.Equal(1, mock.PublishedMediaCountFor(PublishChannel.FacebookPage));
        Assert.Equal(2, mock.ContainerCountFor(PublishChannel.FacebookPage));   // orphan + published

        // 3) Instagram wholly unaffected — finalized on the first pass, one container, one publish.
        Assert.Equal(1, mock.PublishedMediaCountFor(PublishChannel.Instagram));
        Assert.Equal(1, mock.ContainerCountFor(PublishChannel.Instagram));
    }

    [Fact]
    public async Task Facebook_crash_in_publish_window_recovers_and_instagram_is_unaffected()
    {
        var contentItemId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var mock = new MockMetaIntegration { CrashAfterPublishOnChannel = PublishChannel.FacebookPage };

        var ig = await PublishAsync(mock, contentItemId, runId, PublishChannel.Instagram);

        // Variant B: Facebook crashes in the publish→finalize window; the retry re-publishes the
        // committed CreationId (Meta dedups) — never a second FB post.
        await PublishExpectingCrashAsync(mock, contentItemId, runId, PublishChannel.FacebookPage);
        var fb = await PublishAsync(mock, contentItemId, runId, PublishChannel.FacebookPage);

        Assert.Equal(PublishStatus.Published, ig.Status);
        Assert.Equal(PublishStatus.Published, fb.Status);
        Assert.NotEqual(ig.ExternalRef, fb.ExternalRef);

        var igRecord = await ReadRecordAsync(contentItemId, PublishChannel.Instagram);
        var fbRecord = await ReadRecordAsync(contentItemId, PublishChannel.FacebookPage);
        Assert.Equal(ig.ExternalRef, igRecord!.ExternalRef);
        Assert.Equal(fb.ExternalRef, fbRecord!.ExternalRef);

        // Facebook: exactly one post, one container — the retry deduped onto the committed CreationId.
        Assert.Equal(1, mock.PublishedMediaCountFor(PublishChannel.FacebookPage));
        Assert.Equal(1, mock.ContainerCountFor(PublishChannel.FacebookPage));

        // Instagram wholly unaffected.
        Assert.Equal(1, mock.PublishedMediaCountFor(PublishChannel.Instagram));
        Assert.Equal(1, mock.ContainerCountFor(PublishChannel.Instagram));
    }

    [Fact]
    public async Task Instagram_crash_leaves_facebook_unaffected_symmetric()
    {
        var contentItemId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var mock = new MockMetaIntegration { CrashAfterPublishOnChannel = PublishChannel.Instagram };

        // Symmetric check: Facebook publishes cleanly first; Instagram crashes then recovers.
        var fb = await PublishAsync(mock, contentItemId, runId, PublishChannel.FacebookPage);
        await PublishExpectingCrashAsync(mock, contentItemId, runId, PublishChannel.Instagram);
        var ig = await PublishAsync(mock, contentItemId, runId, PublishChannel.Instagram);

        Assert.Equal(PublishStatus.Published, fb.Status);
        Assert.Equal(PublishStatus.Published, ig.Status);
        Assert.NotEqual(ig.ExternalRef, fb.ExternalRef);

        var fbRecord = await ReadRecordAsync(contentItemId, PublishChannel.FacebookPage);
        var igRecord = await ReadRecordAsync(contentItemId, PublishChannel.Instagram);
        Assert.Equal(fb.ExternalRef, fbRecord!.ExternalRef);
        Assert.Equal(ig.ExternalRef, igRecord!.ExternalRef);

        // Facebook wholly unaffected; Instagram one effective post (the retry deduped).
        Assert.Equal(1, mock.PublishedMediaCountFor(PublishChannel.FacebookPage));
        Assert.Equal(1, mock.ContainerCountFor(PublishChannel.FacebookPage));
        Assert.Equal(1, mock.PublishedMediaCountFor(PublishChannel.Instagram));
        Assert.Equal(1, mock.ContainerCountFor(PublishChannel.Instagram));
    }
}
