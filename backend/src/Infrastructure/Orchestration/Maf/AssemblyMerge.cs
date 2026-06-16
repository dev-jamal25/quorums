using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;

namespace Backend.Infrastructure.Orchestration.Maf;

/// <summary>
/// The Supervisor's assembly/join logic, used as the incremental aggregator behind the
/// fan-in barrier that joins the Copywriting ∥ Media Generation fork. MAF's barrier streams
/// each branch message to the aggregator one at a time (<c>agg</c> is null on the first),
/// so the merge is written as a pure, order-independent fold: it takes each branch's
/// <em>disjoint</em> slice (Caption from copywriting, Media from media generation), unions
/// errors/spans/incurred-costs, and — as the sole writer of <see cref="RunState.Draft"/>,
/// <see cref="RunState.Phase"/>, and <see cref="RunState.Budget"/> (DL-020, R3) — reconciles the
/// budget, assembles the (possibly caption-only) draft, and moves the run to the human gate.
/// A propagated <see cref="RunState.FatalError"/> yields no draft (the job fails the run).
/// </summary>
internal static class AssemblyMerge
{
    /// <summary>
    /// Aggregator step. <paramref name="agg"/> is the running merge (null on the first
    /// branch message); <paramref name="msg"/> is the next branch's <see cref="RunState"/>.
    /// </summary>
    public static RunState Fold(RunState? agg, RunState msg)
    {
        var merged = agg is null ? msg : Combine(agg, msg);
        return WithAssembledDraft(merged);
    }

    private static RunState Combine(RunState a, RunState b)
    {
        // Disjoint ownership: exactly one branch carries the Caption, the other the Media.
        var caption = a.Caption ?? b.Caption;
        var media = a.Media ?? b.Media;

        // Both branches forked from the same pre-fork state, so they share the strategy /
        // creative errors and spans; union de-dupes them (errors by value, spans by id).
        var errors = a.Errors.Concat(b.Errors).Distinct().ToList();
        var spans = a.Trace.Spans.Concat(b.Trace.Spans)
            .GroupBy(s => s.SpanId)
            .Select(g => g.First())
            .OrderBy(s => s.StartedAt)
            .ThenBy(s => s.SpanId, StringComparer.Ordinal)
            .ToList();
        var traceId = string.IsNullOrEmpty(a.Trace.TraceId) ? b.Trace.TraceId : a.Trace.TraceId;

        // Per-node costs union by node name (both branches carry the shared pre-fork costs).
        var costs = a.IncurredCosts.Concat(b.IncurredCosts)
            .GroupBy(cost => cost.Node, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        return a with
        {
            Caption = caption,
            Media = media,
            Errors = errors,
            IncurredCosts = costs,
            FatalError = a.FatalError ?? b.FatalError,
            Trace = new TraceRefs(traceId, spans.Select(s => s.SpanId).ToList(), spans),
        };
    }

    private static RunState WithAssembledDraft(RunState state)
    {
        // Sole Budget writer (R3): fold the unioned per-node costs into the budget. Idempotent —
        // a set from the deduped union, so repeated folds over the incremental aggregate are safe.
        var budget = state.Budget with
        {
            TokensSpent = state.IncurredCosts.Sum(cost => cost.Tokens),
            MediaSpent = state.IncurredCosts.Sum(cost => cost.MediaUsd),
        };

        // A fatal node failed → no draft; the job fails the run (DL-022/023). Phase is left as-is
        // (the job overrides the AgentRun status to Failed regardless).
        if (state.FatalError is not null)
        {
            return state with { Draft = null, Budget = budget };
        }

        // A complete draft needs the caption; until both branches have merged it may be absent
        // (transient seed state). A null Media is a VALID caption-only draft (BudgetDegraded, R1).
        var draft = state.Caption is null
            ? null
            : new ContentItemDraft(
                CaptionRef: state.Caption,
                MediaRef: state.Media,
                BrandId: state.BrandId,
                Status: state.Media is null ? "degraded-caption-only" : "pending");

        return state with { Phase = GraphPhase.AwaitingApproval, Draft = draft, Budget = budget };
    }
}
