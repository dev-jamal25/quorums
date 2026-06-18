using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;

namespace Backend.Core.Evaluation;

/// <summary>
/// A read-only projection of one generation run, carrying exactly enough to run every rule-based
/// tool-call check (evaluators.md §1) WITHOUT mutating any frozen contract or <see cref="RunState"/>
/// (DL-020). Everything except the two off-state fields is read straight off the terminal
/// <see cref="RunState"/> + trace by <c>SystemOutputProjector</c>, mirroring <c>RunReviewProjection</c>.
///
/// <para><see cref="InjectedChunkIdsByNode"/> and <see cref="RetryCountsByNode"/> are NOT recoverable
/// from RunState/trace — the executors compute the injected provenance ids then discard them, and the
/// generator's retry loop is internal. They are supplied read-only by mock-mode recording doubles (the
/// existing <c>Recording*</c> pattern); no frozen contract changes to expose them.</para>
/// </summary>
public sealed record SystemOutput(
    // -- run metadata --
    Guid RunId,
    Guid BrandId,
    string TargetSurface,
    IReadOnlyList<string> ContentPillars,
    // -- typed agent outputs --
    StrategyCandidates? Candidates,
    ContentStrategy? Strategy,
    int? ChosenIndex,
    CreativeDirection? Creative,
    Caption? Caption,
    MediaAssetRef? Media,
    ContentItemDraft? Draft,
    // -- budget / degradation --
    bool BudgetDegraded,
    int GeminiCallCount,
    Budget Budget,
    // -- failure isolation --
    IReadOnlyList<ToolError> Errors,
    ToolError? FatalError,
    // -- off-state fields (recording doubles, mock mode) --
    IReadOnlyDictionary<string, IReadOnlyList<string>> InjectedChunkIdsByNode,
    IReadOnlyDictionary<string, int> RetryCountsByNode,
    // -- raw trace (for span-level assertions) --
    TraceRefs Trace)
{
    /// <summary>Canonical node keys — the graph nodes that carry a typed output + grounding.</summary>
    public static class Nodes
    {
        public const string ContentStrategist = "ContentStrategist";
        public const string SupervisorSelection = "SupervisorSelection";
        public const string CreativeDirector = "CreativeDirector";
        public const string Copywriting = "Copywriting";
    }
}
