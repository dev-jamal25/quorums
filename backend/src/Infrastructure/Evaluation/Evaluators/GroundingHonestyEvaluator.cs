using Backend.Core.Evaluation;
using Backend.Core.Generation.Validation;
using Backend.Core.Orchestration.Contracts;

namespace Backend.Infrastructure.Evaluation.Evaluators;

/// <summary>
/// §1.6 Grounding honesty (DL-034) — the deterministic faithfulness floor: a node's claimed
/// <c>grounded</c> set must equal <c>claimedChunkIds ∩ injectedChunkIds</c>, i.e. no output may cite a
/// chunk id that was not injected into its prompt, and <c>grounded</c> must be derived correctly.
/// Re-runs the same <see cref="GroundingValidator.Reconcile"/> the pipeline uses against the injected
/// provenance ids per node (mock-mode recording double) and fails on any divergence.
/// </summary>
public sealed class GroundingHonestyEvaluator : SystemOutputEvaluator
{
    public const string MetricNameConst = "Grounding Honesty";

    protected override string MetricName => MetricNameConst;

    protected override Verdict Evaluate(SystemOutput output, EvalCase evalCase)
    {
        var checks = new (string Node, Grounding? Grounding)[]
        {
            (SystemOutput.Nodes.ContentStrategist, output.Strategy?.Grounding),
            (SystemOutput.Nodes.CreativeDirector, output.Creative?.Grounding),
            (SystemOutput.Nodes.Copywriting, output.Caption?.Grounding),
        };

        foreach (var (node, grounding) in checks)
        {
            if (grounding is null)
            {
                continue;
            }

            var injected = output.InjectedChunkIdsByNode.TryGetValue(node, out var ids) ? ids : [];
            if (!IsHonest(grounding, injected))
            {
                return Verdict.Fail(
                    $"node '{node}' claims chunk ids outside its injected set or mis-derives grounded "
                    + $"(claimed=[{string.Join(",", grounding.ChunkIdsUsed)}], injected=[{string.Join(",", injected)}])");
            }
        }

        return Verdict.Pass("every output's grounded set equals claimed ∩ injected");
    }

    private static bool IsHonest(Grounding grounding, IReadOnlyList<string> injectedProvenanceIds)
    {
        var reconciled = GroundingValidator.Reconcile(grounding, injectedProvenanceIds);

        var claimed = new HashSet<string>(grounding.ChunkIdsUsed ?? [], StringComparer.Ordinal);
        var kept = new HashSet<string>(reconciled.ChunkIdsUsed, StringComparer.Ordinal);

        // claimed == (claimed ∩ injected) means no claimed id was dropped (claimed ⊆ injected);
        // and the derived grounded flag must match what the output asserted.
        return claimed.SetEquals(kept) && reconciled.Grounded == grounding.Grounded;
    }
}
