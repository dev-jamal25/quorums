namespace Backend.Core.Orchestration.Contracts;

/// <summary>
/// Grounding provenance carried on every agent output (DL-027/028). <see cref="ChunkIdsUsed"/>
/// is the set of retrieved-chunk provenance ids the agent claims it grounded in;
/// <see cref="Grounded"/> is <b>derived</b> by the pipeline from the intersection of those
/// claimed ids with the provenance ids actually injected into the prompt (DL-034 R6) — it is
/// never trusted from the model's self-report. Empty retrieval yields
/// <c>Grounded = false</c> and the agent proceeds ungrounded (DL-022).
/// </summary>
public sealed record Grounding(
    bool Grounded,
    IReadOnlyList<string> ChunkIdsUsed,
    Confidence Confidence);
