using Backend.Core.Evaluation;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Backend.Infrastructure.Orchestration.Maf.Nodes;

namespace Backend.Infrastructure.Evaluation;

/// <summary>
/// Projects a terminal <see cref="RunState"/> (+ its trace) into a read-only <see cref="SystemOutput"/>
/// for the evaluators. Pure, no I/O, no mutation — mirrors <c>RunReviewProjection</c> (chosen-index via
/// <see cref="StrategySelection.IndexOf"/>, degradation via the null media ref, Gemini calls off the
/// trace). The per-node grounding provenance (raw claimed + injected chunk ids) is read from the durable
/// trace provenance spans (DL-054) on real <b>and</b> mock runs alike — no recording double. Retry counts
/// are not on the trace, so they remain supplied (mock-mode <c>CountingChatClient</c>); absent → empty.
/// The trace itself is loaded brand-scoped upstream (the harness reads <see cref="RunState"/> under the
/// transaction-scoped bind); this projector is pure over the in-memory state.
/// </summary>
public static class SystemOutputProjector
{
    private const string GeminiTool = "gemini.generate";
    private const string BudgetDegradedMarker = "BudgetDegraded";

    private static readonly IReadOnlyDictionary<string, int> _emptyRetries =
        new Dictionary<string, int>();

    public static SystemOutput Project(
        RunState state,
        IReadOnlyDictionary<string, int>? retryCountsByNode = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        var candidates = state.Candidates?.Candidates ?? [];
        int? chosenIndex = state.Strategy is { } strategy
            ? StrategySelection.IndexOf(candidates, strategy)
            : null;

        var (claimedByNode, injectedByNode) = ReadProvenance(state.Trace);

        // Caption-only draft (null media ref) is the degraded signal — matches RunReviewProjection;
        // corroborated by the Media node's BudgetDegraded trace event for the generation-only path.
        var budgetDegraded = state.Draft is { MediaRef: null }
            || (state.Media is null && HasBudgetDegradedSpan(state.Trace));

        // Count EVERY gemini.generate span — ok AND error/cancelled. "Zero Gemini calls on a budget
        // breach" must mean zero ATTEMPTS: on a breach the gate trips upstream so no span opens at all
        // (count 0); but if the gate ever failed to trip and the call then errored, that opened an error
        // span — counting only ok-spans would let an errored attempt read as zero (DL-023 hardening).
        var geminiCallCount = state.Trace.Spans
            .Count(span => string.Equals(span.Tool, GeminiTool, StringComparison.Ordinal));

        return new SystemOutput(
            RunId: state.RunId,
            BrandId: state.BrandId,
            TargetSurface: state.TargetSurface,
            ContentPillars: state.ContentPillars,
            Candidates: state.Candidates,
            Strategy: state.Strategy,
            ChosenIndex: chosenIndex,
            Creative: state.Creative,
            Caption: state.Caption,
            Media: state.Media,
            Draft: state.Draft,
            BudgetDegraded: budgetDegraded,
            GeminiCallCount: geminiCallCount,
            Budget: state.Budget,
            Errors: state.Errors,
            FatalError: state.FatalError,
            ClaimedChunkIdsByNode: claimedByNode,
            InjectedChunkIdsByNode: injectedByNode,
            RetryCountsByNode: retryCountsByNode ?? _emptyRetries,
            Trace: state.Trace);
    }

    private static bool HasBudgetDegradedSpan(TraceRefs trace) =>
        trace.Spans.Any(span =>
            span.Detail is { } detail && detail.Contains(BudgetDegradedMarker, StringComparison.Ordinal));

    // DL-054: each grounding executor wrote a per-node provenance span (raw claimed + injected) to the
    // durable trace before reconcile. Read them back keyed by the eval node — the single source on real
    // and mock runs. A later span for a node (e.g. a regenerate re-run) overwrites the earlier one.
    private static (IReadOnlyDictionary<string, IReadOnlyList<string>> Claimed,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Injected) ReadProvenance(TraceRefs trace)
    {
        var claimed = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var injected = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var span in trace.Spans)
        {
            if (GroundingProvenance.TryParse(span) is not { } detail || ToEvalNode(span.Node) is not { } node)
            {
                continue;
            }

            claimed[node] = detail.ClaimedChunkIds;
            injected[node] = detail.InjectedChunkIds;
        }

        return (claimed, injected);
    }

    private static string? ToEvalNode(string traceNode) => traceNode switch
    {
        "strategy" => SystemOutput.Nodes.ContentStrategist,
        "creative" => SystemOutput.Nodes.CreativeDirector,
        "copywriting" => SystemOutput.Nodes.Copywriting,
        _ => null,
    };
}
