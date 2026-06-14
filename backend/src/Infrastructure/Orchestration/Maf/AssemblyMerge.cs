using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;

namespace Backend.Infrastructure.Orchestration.Maf;

/// <summary>
/// The Supervisor's assembly/join logic, used as the incremental aggregator behind the
/// fan-in barrier that joins the Copywriting ∥ Media Generation fork. MAF's barrier streams
/// each branch message to the aggregator one at a time (<c>agg</c> is null on the first),
/// so the merge is written as a pure, order-independent fold: it takes each branch's
/// <em>disjoint</em> slice (Caption from copywriting, Media from media generation), unions
/// errors by value and trace spans by id, then — as the sole writer of <see cref="RunState.Draft"/>
/// and <see cref="RunState.Phase"/> (DL-020) — assembles the draft and moves the run to the
/// human gate.
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

        return a with
        {
            Caption = caption,
            Media = media,
            Errors = errors,
            Trace = new TraceRefs(traceId, spans.Select(s => s.SpanId).ToList(), spans),
        };
    }

    private static RunState WithAssembledDraft(RunState state)
    {
        // A complete draft needs the caption; until both branches have merged it may be
        // absent (transient seed state). Only the final aggregate is surfaced as output.
        var draft = state.Caption is null
            ? null
            : new ContentItemDraft(
                CaptionRef: state.Caption,
                MediaRef: state.Media,
                BrandId: state.BrandId,
                Status: state.Media is null ? "degraded-caption-only" : "pending");

        return state with { Phase = GraphPhase.AwaitingApproval, Draft = draft };
    }
}
