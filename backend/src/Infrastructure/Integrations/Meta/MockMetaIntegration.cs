using Backend.Core.Common;
using Backend.Core.Integrations;
using Backend.Core.Orchestration.Contracts;

namespace Backend.Infrastructure.Integrations.Meta;

/// <summary>
/// Deterministic, network-free Meta publisher (DL-004). Returns a stable fake
/// external reference derived from the content id, so a retried publish yields the
/// same <c>mock://meta/{id}</c> ref rather than a second post — idempotent by
/// construction (DL-022). Selected when <c>Meta:Mode=mock</c> (the default); a full
/// run completes with zero live Meta calls.
/// </summary>
public sealed class MockMetaIntegration : IMetaIntegration
{
    public Task<PublishResult> PublishAsync(
        PublishRequest request,
        CancellationToken cancellationToken = default)
    {
        var externalId = DeterministicGuid.From(request.ContentItemId, "meta");
        var result = new PublishResult(
            ExternalRef: $"mock://meta/{externalId}",
            Status: "published",
            Error: null);

        return Task.FromResult(result);
    }
}
