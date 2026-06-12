namespace Backend.Core.Orchestration;

public sealed record TraceRefs(
    string TraceId,
    List<string> SpanIds);
