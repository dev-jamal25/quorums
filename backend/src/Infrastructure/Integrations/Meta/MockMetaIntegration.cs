using System.Collections.Concurrent;
using Backend.Core.Common;
using Backend.Core.Domain;
using Backend.Core.Integrations;
using Backend.Core.Orchestration.Contracts;

namespace Backend.Infrastructure.Integrations.Meta;

/// <summary>
/// Deterministic, network-free Meta publisher (DL-004, DL-038). Models the real two-step shape —
/// create container → poll → publish container — with in-memory state that mirrors Meta's
/// server-side behaviour: a unique container id per create (so a crashed create leaves a real orphan),
/// and a re-publish of an already-published container deduped to the SAME media id (no second post,
/// DL-039). The published media id is keyed on the <c>ContentItemId</c> idempotency key, so it is
/// stable across retries.
/// <para>For CI it exposes deterministic injection: both failure classes (returned typed, never
/// thrown) and one-shot crash points in the two durability windows the robust mechanism guards —
/// after create (before the component persists the <c>CreationId</c>) and after publish (before the
/// component records the <c>ExternalRef</c>). A crash throws AFTER the mock's side effect, exactly as
/// a process death would.</para>
/// </summary>
public sealed class MockMetaIntegration : IMetaIntegration
{
    private readonly ConcurrentDictionary<string, Guid> _containers = new();   // creationId -> contentItemId
    private readonly ConcurrentDictionary<string, byte> _published = new();    // creationIds that have been published

    // Deterministic test injection (defaults: clean two-step success).
    public PublishStatus? FailCreateWith { get; set; }
    public PublishStatus? FailPollWith { get; set; }
    public PublishStatus? FailPublishWith { get; set; }
    public bool CrashAfterCreateOnce { get; set; }
    public bool CrashAfterPublishOnce { get; set; }

    /// <summary>Distinct published media — the headline idempotency assertion ("exactly one post").</summary>
    public int PublishedMediaCount => _published.Count;

    /// <summary>Distinct containers created — proves a crashed create leaves an orphan.</summary>
    public int ContainerCount => _containers.Count;

    public Task<ContainerResult> CreateContainerAsync(PublishRequest request, CancellationToken cancellationToken = default)
    {
        if (FailCreateWith is { } failure)
        {
            return Task.FromResult(new ContainerResult(null, failure, $"mock create {failure}"));
        }

        // A real create returns a fresh container id every call, so a crash before the CreationId is
        // persisted leaves a distinct orphan container (never published).
        var creationId = $"mock-container-{Guid.NewGuid():N}";
        _containers[creationId] = request.ContentItemId;

        if (CrashAfterCreateOnce)
        {
            CrashAfterCreateOnce = false;
            throw new InvalidOperationException("Simulated crash after create, before CreationId persisted.");
        }

        return Task.FromResult(new ContainerResult(creationId, null, null));
    }

    public Task<ContainerStatus> PollContainerAsync(string creationId, CancellationToken cancellationToken = default)
    {
        if (FailPollWith is { } failure)
        {
            return Task.FromResult(new ContainerStatus(false, failure, $"mock poll {failure}"));
        }

        return Task.FromResult(new ContainerStatus(true, null, null));
    }

    public Task<PublishResult> PublishContainerAsync(string creationId, CancellationToken cancellationToken = default)
    {
        if (FailPublishWith is { } failure)
        {
            return Task.FromResult(new PublishResult(failure, null, $"mock publish {failure}", null));
        }

        if (!_containers.TryGetValue(creationId, out var contentItemId))
        {
            return Task.FromResult(new PublishResult(
                PublishStatus.TerminalFailure, null, $"unknown container '{creationId}'", null));
        }

        // The published media id is keyed on the idempotency key, so it is stable across retries.
        var mediaId = DeterministicGuid.From(contentItemId, "meta").ToString();
        var externalRef = $"mock://meta/{mediaId}";

        // Idempotent: a re-publish of an already-published container is deduped — no second post.
        _published.TryAdd(creationId, 0);

        if (CrashAfterPublishOnce)
        {
            CrashAfterPublishOnce = false;
            throw new InvalidOperationException("Simulated crash after publish, before ExternalRef recorded.");
        }

        return Task.FromResult(new PublishResult(
            PublishStatus.Published, externalRef, null, new EngagementKeys(mediaId, null)));
    }
}
