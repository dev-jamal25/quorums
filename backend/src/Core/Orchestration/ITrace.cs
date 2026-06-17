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

    /// <summary>
    /// Records one LLM call as a Langfuse <em>generation</em> observation on the run's trace — the
    /// model and the input/output token usage (which Langfuse turns into cost). Fire-and-forget by
    /// shape: it is NOT threaded through <see cref="RunState"/> (the LLM client holds no state slice),
    /// so it returns nothing. The local recorder no-ops it; Langfuse posts it best-effort and a failed
    /// post never fails the run. The trace id derives from <paramref name="runId"/>, so it matches the
    /// node spans even though the generation precedes the node's own span.
    /// </summary>
    Task RecordGenerationAsync(
        Guid runId,
        Guid brandId,
        string name,
        string? model,
        long? inputTokens,
        long? outputTokens,
        string? input,
        string? output,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        CancellationToken cancellationToken = default);
}
