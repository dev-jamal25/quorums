using Backend.Core.Orchestration;

namespace Backend.Infrastructure.Tracing;

/// <summary>
/// Pure assembly of the trace surface shared by both <see cref="ITrace"/> impls. The first appended
/// span assigns the trace id — derived from the run id (<see cref="TraceId"/>) so it is stable across
/// the durable seam AND so a generation emitted before the first span can compute the same id from the
/// run id. Every span gets a fresh id appended to both the id list and the span list.
/// </summary>
internal static class TraceAssembler
{
    /// <summary>The deterministic Langfuse trace id for a run — shared by its spans and generations.</summary>
    public static string TraceId(Guid runId) => runId.ToString("N");

    public static TraceRefs Append(
        TraceRefs current,
        Guid runId,
        string node,
        string? tool,
        string status,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        string? error,
        string? detail = null)
    {
        var traceId = string.IsNullOrEmpty(current.TraceId) ? TraceId(runId) : current.TraceId;

        var spanId = Guid.NewGuid().ToString("N");
        var span = new TraceSpan(spanId, node, tool, status, startedAt, endedAt, error, detail);

        var spanIds = new List<string>(current.SpanIds ?? []) { spanId };
        var spans = new List<TraceSpan>(current.Spans ?? []) { span };

        return new TraceRefs(traceId, spanIds, spans);
    }
}
