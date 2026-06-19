using Backend.Core.Evaluation;
using Backend.Core.Generation.Validation;

namespace Backend.Infrastructure.Evaluation.Evaluators;

/// <summary>
/// §1.4 Selection-index range — the Supervisor's <c>chosenIndex</c> is within <c>[0, candidates.Count)</c>.
/// Delegates to the same <see cref="SelectionValidator"/> the Supervisor selection node uses. A run that
/// never selected (no strategy / no candidates) is not a selection violation — it is the schema
/// evaluator's concern.
/// </summary>
public sealed class SelectionIndexEvaluator : SystemOutputEvaluator
{
    public const string MetricNameConst = "Selection Index Range";

    protected override string MetricName => MetricNameConst;

    protected override Verdict Evaluate(SystemOutput output, EvalCase evalCase)
    {
        var candidateCount = output.Candidates?.Candidates.Count ?? 0;
        if (output.ChosenIndex is not { } chosenIndex || candidateCount == 0)
        {
            return Verdict.Pass("no selection was made (nothing to range-check)");
        }

        var result = SelectionValidator.Validate(chosenIndex, candidateCount);
        return result.IsValid
            ? Verdict.Pass($"chosenIndex {chosenIndex} is within [0, {candidateCount})")
            : Verdict.Fail(result.Error ?? "selection index out of range");
    }
}
