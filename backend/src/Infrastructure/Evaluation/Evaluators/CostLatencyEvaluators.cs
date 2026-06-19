using Backend.Core.Generation.Cost;
using Microsoft.Extensions.AI.Evaluation;

namespace Backend.Infrastructure.Evaluation.Evaluators;

/// <summary>
/// The §3 cost &amp; latency observability evaluators (DL-022/023) as custom
/// Microsoft.Extensions.AI.Evaluation <see cref="IEvaluator"/>s — no LLM, no threshold, tracked-only.
/// Persisted RLS-scoped per eval run by the harness alongside the rule-based metrics. The only configured
/// dependency is the config-bound <see cref="CostPrices"/> the cost evaluator converts usage with.
/// </summary>
public static class CostLatencyEvaluators
{
    public static IReadOnlyList<IEvaluator> All(CostPrices prices) =>
    [
        new CostEvaluator(prices),
        new LatencyEvaluator(),
    ];
}
