using Backend.Core.Generation.PlatformConstraints;
using Microsoft.Extensions.AI.Evaluation;

namespace Backend.Infrastructure.Evaluation.Evaluators;

/// <summary>
/// The deterministic, merge-blocking rule-based tool-call evaluators (evaluators.md §1), as
/// Microsoft.Extensions.AI.Evaluation <see cref="IEvaluator"/>s. Node-generic where possible (DL-052),
/// so activating the stub agents needs no new evaluator. The only configured dependency is the
/// <see cref="PlatformConstraintSet"/> (config-bound limits) the constraints evaluator reuses.
/// </summary>
public static class RuleBasedEvaluators
{
    public static IReadOnlyList<IEvaluator> All(PlatformConstraintSet constraints) =>
    [
        new SchemaValidityEvaluator(),
        new BoundedRetryEvaluator(),
        new PlatformConstraintsEvaluator(constraints),
        new SelectionIndexEvaluator(),
        new ObjectivePillarEvaluator(),
        new GroundingHonestyEvaluator(),
        new BudgetDegradationEvaluator(),
    ];
}
