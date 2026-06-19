using System.Collections.Concurrent;
using Backend.Core.Domain;
using Backend.Core.Integrations;
using Backend.Core.Orchestration.Contracts;
using Backend.Infrastructure.Integrations.Meta;

namespace Backend.IntegrationTests.Support;

/// <summary>
/// Test double that wraps <see cref="MockMetaIntegration"/> and records every publish-container call
/// so a test can assert "no double publish": <see cref="PublishCount"/> counts publish invocations
/// and <see cref="PublishedRefs"/> exposes the external references (distinct count == 1 means one
/// post even if the publish step ran twice).
/// </summary>
public sealed class RecordingMetaIntegration : IMetaIntegration
{
    private readonly MockMetaIntegration _inner = new();
    private readonly ConcurrentQueue<string?> _refs = new();

    public IReadOnlyCollection<string?> PublishedRefs => _refs;

    public int PublishCount => _refs.Count;

    public Task<ContainerResult> CreateContainerAsync(PublishRequest request, CancellationToken cancellationToken = default)
        => _inner.CreateContainerAsync(request, cancellationToken);

    public Task<ContainerStatus> PollContainerAsync(PublishChannel channel, string creationId, CancellationToken cancellationToken = default)
        => _inner.PollContainerAsync(channel, creationId, cancellationToken);

    public async Task<PublishResult> PublishContainerAsync(PublishChannel channel, string creationId, CancellationToken cancellationToken = default)
    {
        var result = await _inner.PublishContainerAsync(channel, creationId, cancellationToken).ConfigureAwait(false);
        _refs.Enqueue(result.ExternalRef);
        return result;
    }
}
