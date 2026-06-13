namespace Backend.Core.Orchestration;

/// <summary>
/// A single recorded span — one orchestration node or one tool call — with its
/// status and timing. Persisted inside <see cref="TraceRefs"/> on the checkpoint so
/// the assembled trace survives the pause/resume seam and is replayable behind
/// <c>GET /runs/{id}/trace</c> (§10).
/// </summary>
public sealed record TraceSpan(
    string SpanId,
    string Node,
    string? Tool,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string? Error);
