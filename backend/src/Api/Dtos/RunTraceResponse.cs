namespace Backend.Api.Dtos;

/// <summary>
/// The assembled trace for a run, read from the checkpoint's <c>RunState.Trace</c>
/// (§10). One entry per orchestration node and per tool call (media write, publish).
/// </summary>
public sealed record RunTraceResponse(
    Guid RunId,
    string TraceId,
    IReadOnlyList<TraceSpanDto> Spans);

public sealed record TraceSpanDto(
    string SpanId,
    string Node,
    string? Tool,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    double DurationMs,
    string? Error);
