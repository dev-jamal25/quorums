using Backend.Core.Domain;
using Backend.Core.Orchestration.Contracts;

namespace Backend.Core.Integrations;

/// <summary>
/// The Meta publishing seam (DL-004, DL-038, DL-055). Content-publish is a two-step-plus-poll flow —
/// create a media container/unpublished photo, poll until it is processed, then publish — and is NOT
/// naturally idempotent. ONE channel-aware client serves both surfaces: it branches Instagram (media
/// container → <c>media_publish</c>) versus Facebook Page (unpublished photo → feed post) internally
/// on the request's <c>Channel</c>; it is never two interfaces. The steps are exposed SEPARATELY so the
/// publish-execution component can persist the container <c>CreationId</c> BETWEEN create and publish;
/// that persisted id is what makes a crash-and-retry safe (re-publish the same container/photo, which
/// Meta dedups, rather than create a second post — DL-039). A single opaque publish call cannot.
/// <para>The demo runs on <c>MockMetaIntegration</c> (<c>Meta:Mode=mock</c>); <c>LiveMetaIntegration</c>
/// is the same shape behind <c>Meta:Mode=live</c>. Failures are returned CLASSIFIED in the typed
/// results — never thrown for the caller to sniff.</para>
/// </summary>
public interface IMetaIntegration
{
    /// <summary>
    /// Create the media container (Instagram) or unpublished photo (Facebook Page) for
    /// <paramref name="request"/>.<c>Channel</c>. Returns its <c>CreationId</c>, or a classified failure.
    /// </summary>
    Task<ContainerResult> CreateContainerAsync(PublishRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Poll a created container on <paramref name="channel"/> until it is processed (or a classified
    /// failure). Facebook photos are immediate-ready; Instagram containers may still be processing.
    /// </summary>
    Task<ContainerStatus> PollContainerAsync(PublishChannel channel, string creationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publish a processed container on <paramref name="channel"/>, returning the published media id.
    /// Idempotent on the <paramref name="creationId"/>: re-publishing an already-published container
    /// returns the same media id rather than creating a second post (DL-039).
    /// </summary>
    Task<PublishResult> PublishContainerAsync(PublishChannel channel, string creationId, CancellationToken cancellationToken = default);
}
