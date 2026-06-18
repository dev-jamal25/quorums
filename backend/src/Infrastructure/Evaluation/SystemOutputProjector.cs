using Backend.Core.Evaluation;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;

namespace Backend.Infrastructure.Evaluation;

/// <summary>
/// Projects a terminal <see cref="RunState"/> (+ its trace) into a read-only <see cref="SystemOutput"/>
/// for the evaluators. Pure, no I/O, no mutation — mirrors <c>RunReviewProjection</c> (chosen-index via
/// <see cref="StrategySelection.IndexOf"/>, degradation via the null media ref, Gemini calls off the
/// trace). The two off-state maps (injected chunk-ids, retry counts) are not on RunState/trace and are
/// supplied by mock-mode recording doubles; absent, they default to empty.
/// </summary>
public static class SystemOutputProjector
{
    private const string GeminiTool = "gemini.generate";
    private const string OkStatus = "ok";
    private const string BudgetDegradedMarker = "BudgetDegraded";

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _emptyInjected =
        new Dictionary<string, IReadOnlyList<string>>();

    private static readonly IReadOnlyDictionary<string, int> _emptyRetries =
        new Dictionary<string, int>();

    public static SystemOutput Project(
        RunState state,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? injectedChunkIdsByNode = null,
        IReadOnlyDictionary<string, int>? retryCountsByNode = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        var candidates = state.Candidates?.Candidates ?? [];
        int? chosenIndex = state.Strategy is { } strategy
            ? StrategySelection.IndexOf(candidates, strategy)
            : null;

        // Caption-only draft (null media ref) is the degraded signal — matches RunReviewProjection;
        // corroborated by the Media node's BudgetDegraded trace event for the generation-only path.
        var budgetDegraded = state.Draft is { MediaRef: null }
            || (state.Media is null && HasBudgetDegradedSpan(state.Trace));

        var geminiCallCount = state.Trace.Spans
            .Count(span => string.Equals(span.Tool, GeminiTool, StringComparison.Ordinal)
                && string.Equals(span.Status, OkStatus, StringComparison.Ordinal));

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
            InjectedChunkIdsByNode: injectedChunkIdsByNode ?? _emptyInjected,
            RetryCountsByNode: retryCountsByNode ?? _emptyRetries,
            Trace: state.Trace);
    }

    private static bool HasBudgetDegradedSpan(TraceRefs trace) =>
        trace.Spans.Any(span =>
            span.Detail is { } detail && detail.Contains(BudgetDegradedMarker, StringComparison.Ordinal));
}
