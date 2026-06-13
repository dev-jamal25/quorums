namespace Backend.Core.Orchestration;

/// <summary>
/// The trace surface carried in <see cref="RunState"/> (§10). <see cref="TraceId"/>
/// and <see cref="SpanIds"/> are the Langfuse references that survive the
/// pause/resume seam; <see cref="Spans"/> additionally carries the assembled span
/// detail (node/tool, status, timing) so <c>GET /runs/{id}/trace</c> can return a
/// full trace read straight from the checkpoint, with or without a live Langfuse.
/// </summary>
public sealed record TraceRefs(
    string TraceId,
    List<string> SpanIds,
    List<TraceSpan> Spans);
