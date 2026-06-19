using Backend.Core.Domain;
using Backend.Core.Integrations;
using Backend.Core.Orchestration.Contracts;

namespace Backend.Infrastructure.Integrations.Meta;

/// <summary>
/// Clean seam for the optional live Meta integration (selected by <c>Meta:Mode=live</c>). The
/// per-brand token decrypt (Vault Transit, DL-011) and the real Graph API two-step calls for BOTH
/// channels (Instagram + Facebook Page, DL-055) would live here, at publish time only. Deliberately
/// not implemented — the demo is mock-only; present so the wiring exists, throws if exercised. Slice 2
/// fills in the real Graph calls; this slice only conforms its signatures to the channel-aware contract.
/// </summary>
public sealed class LiveMetaIntegration : IMetaIntegration
{
    private const string NotImplementedMessage =
        "Live Meta publishing is not implemented (the demo is mock-only). Set Meta:Mode=mock.";

    public Task<ContainerResult> CreateContainerAsync(PublishRequest request, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(NotImplementedMessage);

    public Task<ContainerStatus> PollContainerAsync(PublishChannel channel, string creationId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(NotImplementedMessage);

    public Task<PublishResult> PublishContainerAsync(PublishChannel channel, string creationId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(NotImplementedMessage);
}
