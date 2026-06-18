using Backend.Core.Orchestration;

namespace Backend.Infrastructure.Tracing;

/// <summary>
/// No-op-to-Langfuse trace recorder: assembles the trace in-process and persists it
/// via <see cref="TraceRefs"/> on the checkpoint, with no network call. Selected when
/// Langfuse is not configured (keys empty) — the run must never depend on Langfuse
/// being present. Generations are a Langfuse-only enrichment, so they no-op here.
/// </summary>
public sealed class LocalTraceRecorder : ITrace
{
    public Task<TraceRefs> RecordAsync(
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
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            TraceAssembler.Append(current, runId, node, tool, status, startedAt, endedAt, errorMessage, detail));

    public Task RecordGenerationAsync(
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
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
