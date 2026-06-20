using Backend.Core.Domain;
using Backend.Core.Integrations;
using Backend.Infrastructure.Integrations.Meta;
using Xunit;

namespace Backend.UnitTests.Integrations;

/// <summary>
/// DL-058 mock fidelity: the mock must model REAL Meta on the video surface — re-publishing a committed
/// container does NOT dedup (a reel re-media_publish errors / a re-attached FB video double-posts), so a
/// second post is prevented by the coordinator's idempotency guard, NOT a pretend dedup. The image surface
/// keeps the server-side dedup (DL-039). This pins the fidelity the dual-channel idempotency proof relies on.
/// </summary>
public sealed class MockMetaVideoFidelityTests
{
    private static PublishRequest Request(PublishChannel channel, PostSurface surface) =>
        new(Guid.NewGuid(), channel, "target", surface, "https://public/x", "caption", [], "token");

    [Theory]
    [InlineData(PublishChannel.Instagram)]
    [InlineData(PublishChannel.FacebookPage)]
    public async Task Video_republish_of_a_committed_container_does_not_dedup(PublishChannel channel)
    {
        var mock = new MockMetaIntegration();
        var created = await mock.CreateContainerAsync(Request(channel, PostSurface.Reel));

        await mock.PublishContainerAsync(channel, created.CreationId!);
        await mock.PublishContainerAsync(channel, created.CreationId!); // re-publish the SAME container

        // Real Meta does not dedup a re-published video → two posts. (Production is protected by the guard.)
        Assert.Equal(2, mock.PublishedMediaCount);
        Assert.Equal(2, mock.PublishedMediaCountFor(channel));
    }

    [Theory]
    [InlineData(PublishChannel.Instagram)]
    [InlineData(PublishChannel.FacebookPage)]
    public async Task Image_republish_of_a_committed_container_dedups(PublishChannel channel)
    {
        var mock = new MockMetaIntegration();
        var created = await mock.CreateContainerAsync(Request(channel, PostSurface.FeedImage));

        await mock.PublishContainerAsync(channel, created.CreationId!);
        await mock.PublishContainerAsync(channel, created.CreationId!);

        // An image container is server-side deduped on its committed creation id (DL-039) → one post.
        Assert.Equal(1, mock.PublishedMediaCount);
    }
}
