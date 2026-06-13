using Backend.Core.Orchestration.Contracts;

namespace Backend.Core.Integrations;

/// <summary>
/// The Meta publishing seam (DL-004, DL-005). The demo runs entirely on
/// <c>MockMetaIntegration</c> (selected by <c>Meta:Mode=mock</c>); a live
/// implementation is optional and sits behind the same interface. Publishing is the
/// human-gated, paid-side action — it only ever runs in the <c>ResumeRun</c> segment
/// after an approval is recorded.
/// </summary>
public interface IMetaIntegration
{
    Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken cancellationToken = default);
}
