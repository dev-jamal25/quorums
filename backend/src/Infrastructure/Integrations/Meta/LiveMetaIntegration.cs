using Backend.Core.Integrations;
using Backend.Core.Orchestration.Contracts;

namespace Backend.Infrastructure.Integrations.Meta;

/// <summary>
/// Clean seam for the optional live Meta integration (selected by
/// <c>Meta:Mode=live</c>). Deliberately not implemented in slice c2 — the demo is
/// mock-only. The token decrypt and Graph API calls would live here, at publish time
/// only (DL-011). Present so the wiring exists; throws if exercised.
/// </summary>
public sealed class LiveMetaIntegration : IMetaIntegration
{
    public Task<PublishResult> PublishAsync(
        PublishRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(
            "Live Meta publishing is not implemented (slice c2 is mock-only). Set Meta:Mode=mock.");
}
