namespace Backend.Core.Orchestration;

/// <summary>
/// Tracing seam (§10). Records one completed span — an orchestration node or a tool
/// call — and returns the updated <see cref="TraceRefs"/> to thread back into
/// <see cref="RunState"/>. The first recorded span assigns the trace id, so the same
/// trace continues across the ExecuteRun → ResumeRun seam.
/// <para>
/// Treated like Vault: optional and config-gated. When Langfuse is configured the
/// span is also posted to it (best-effort); when it is not, recording degrades to a
/// no-op local recorder that still populates <see cref="TraceRefs"/>. A tracing
/// failure must never fail the run.
/// </para>
/// </summary>
public interface ITrace
{
    Task<TraceRefs> RecordAsync(
        TraceRefs current,
        Guid runId,
        Guid brandId,
        string node,
        string? tool,
        string status,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        string? errorMessage,
        string? detail = null,
        CancellationToken cancellationToken = default);
}
