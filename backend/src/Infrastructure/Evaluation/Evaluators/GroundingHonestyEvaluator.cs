using Backend.Core.Evaluation;
using Backend.Core.Generation.Validation;
using Backend.Core.Orchestration.Contracts;

namespace Backend.Infrastructure.Evaluation.Evaluators;

/// <summary>
/// §1.6 Grounding honesty (DL-034 / DL-054) — the deterministic faithfulness floor. Audits the
/// <b>raw, pre-reconcile</b> claimed chunk ids against the injected ids, <b>per node</b>: a node that
/// claimed a chunk id not injected into its prompt is a faithfulness violation. The claimed + injected
/// sets are sourced from the durable per-node trace provenance (DL-054) via
/// <see cref="SystemOutput.ClaimedChunkIdsByNode"/> / <see cref="SystemOutput.InjectedChunkIdsByNode"/> —
/// NOT the output's post-reconcile <c>chunkIdsUsed</c>, which the pipeline already reduced to
/// <c>claimed ∩ injected</c> (⊆ injected by construction, so a check against it passes trivially).
/// Delegates the intersection to the same <see cref="GroundingValidator.Reconcile"/> the pipeline uses.
/// </summary>
public sealed class GroundingHonestyEvaluator : SystemOutputEvaluator
{
    public const string MetricNameConst = "Grounding Honesty";

    protected override string MetricName => MetricNameConst;

    protected override Verdict Evaluate(SystemOutput output, EvalCase evalCase)
    {
        foreach (var (node, claimed) in output.ClaimedChunkIdsByNode)
        {
            var injected = output.InjectedChunkIdsByNode.TryGetValue(node, out var ids) ? ids : [];
            if (!IsSubset(claimed, injected))
            {
                var unsupported = claimed.Where(id => !injected.Contains(id, StringComparer.Ordinal));
                return Verdict.Fail(
                    $"node '{node}' claims chunk ids not injected into its prompt "
                    + $"[{string.Join(",", unsupported)}] (claimed=[{string.Join(",", claimed)}], injected=[{string.Join(",", injected)}])");
            }
        }

        return Verdict.Pass("every node's raw claimed ids are a subset of its injected ids");
    }

    // raw claimed ⊆ injected, via the same reconcile (claimed ∩ injected) the pipeline applies:
    // claimed ⊆ injected iff (claimed ∩ injected) == claimed as sets.
    private static bool IsSubset(IReadOnlyList<string> claimed, IReadOnlyList<string> injected)
    {
        var reconciled = GroundingValidator.Reconcile(
            new Grounding(Grounded: true, ChunkIdsUsed: claimed, Confidence.Low), injected);

        var claimedSet = new HashSet<string>(claimed ?? [], StringComparer.Ordinal);
        var keptSet = new HashSet<string>(reconciled.ChunkIdsUsed, StringComparer.Ordinal);
        return claimedSet.SetEquals(keptSet);
    }
}
