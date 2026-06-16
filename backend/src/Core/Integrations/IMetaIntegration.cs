using Backend.Core.Orchestration.Contracts;

namespace Backend.Core.Integrations;

/// <summary>
/// The Meta publishing seam (DL-004, DL-038). Instagram content-publish is a two-step-plus-poll
/// flow — create a media container, poll until it is processed, then publish the container — and is
/// NOT naturally idempotent. The interface exposes the steps SEPARATELY so the publish-execution
/// component can persist the container <c>CreationId</c> BETWEEN create and publish; that persisted
/// id is what makes a crash-and-retry safe (re-publish the same container, which Meta dedups, rather
/// than create a second post — DL-039). A single opaque publish call cannot support that.
/// <para>The demo runs on <c>MockMetaIntegration</c> (<c>Meta:Mode=mock</c>); <c>LiveMetaIntegration</c>
/// is the same shape behind <c>Meta:Mode=live</c>. Failures are returned CLASSIFIED in the typed
/// results — never thrown for the caller to sniff.</para>
/// </summary>
public interface IMetaIntegration
{
    /// <summary>Create the media container. Returns its <c>CreationId</c>, or a classified failure.</summary>
    Task<ContainerResult> CreateContainerAsync(PublishRequest request, CancellationToken cancellationToken = default);

    /// <summary>Poll a created container until it is processed (or a classified failure).</summary>
    Task<ContainerStatus> PollContainerAsync(string creationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publish a processed container, returning the published media id. Idempotent on the
    /// <paramref name="creationId"/>: re-publishing an already-published container returns the same
    /// media id rather than creating a second post (DL-039).
    /// </summary>
    Task<PublishResult> PublishContainerAsync(string creationId, CancellationToken cancellationToken = default);
}
