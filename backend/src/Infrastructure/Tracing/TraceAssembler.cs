using Backend.Core.Orchestration;

namespace Backend.Infrastructure.Tracing;

/// <summary>
/// Pure assembly of the trace surface shared by both <see cref="ITrace"/> impls. The
/// first appended span assigns the trace id; every span gets a fresh id appended to
/// both the id list (the frozen Langfuse references) and the detail list.
/// </summary>
internal static class TraceAssembler
{
    public static TraceRefs Append(
        TraceRefs current,
        string node,
        string? tool,
        string status,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        string? error)
    {
        var traceId = string.IsNullOrEmpty(current.TraceId)
            ? Guid.NewGuid().ToString("N")
            : current.TraceId;

        var spanId = Guid.NewGuid().ToString("N");
        var span = new TraceSpan(spanId, node, tool, status, startedAt, endedAt, error);

        var spanIds = new List<string>(current.SpanIds ?? []) { spanId };
        var spans = new List<TraceSpan>(current.Spans ?? []) { span };

        return new TraceRefs(traceId, spanIds, spans);
    }
}
