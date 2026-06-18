using Backend.Core.Evaluation;
using Backend.Core.Generation.Validation;
using Backend.Core.Orchestration.Contracts;

namespace Backend.Infrastructure.Evaluation.Evaluators;

/// <summary>
/// §1.5 Objective / pillar validity — <c>objective</c> is a defined member of the fixed enum, and
/// <c>pillar</c> validates against the brand playbook's pillar list at receipt (DL-026). Delegates pillar
/// checking to the same <see cref="PillarValidator"/> the Content Strategist node uses;
/// <see cref="PillarStatus.NoPillarsDefined"/> is an explicit (non-silent) skip, never a hidden pass.
/// </summary>
public sealed class ObjectivePillarEvaluator : SystemOutputEvaluator
{
    public const string MetricNameConst = "Objective And Pillar Validity";

    protected override string MetricName => MetricNameConst;

    protected override Verdict Evaluate(SystemOutput output, EvalCase evalCase)
    {
        if (output.Strategy is not { } strategy)
        {
            return Verdict.Pass("no chosen strategy to validate");
        }

        if (!Enum.IsDefined(strategy.Objective))
        {
            return Verdict.Fail($"objective '{strategy.Objective}' is not a defined Objective value");
        }

        return PillarValidator.Check(strategy.Pillar, output.ContentPillars) switch
        {
            PillarStatus.InList => Verdict.Pass($"objective '{strategy.Objective}' valid; pillar '{strategy.Pillar}' is in the brand playbook"),
            PillarStatus.NoPillarsDefined => Verdict.Pass($"objective '{strategy.Objective}' valid; no structured brand pillars to check (skipped, not a silent pass)"),
            _ => Verdict.Fail(PillarValidator.DescribeMiss(strategy.Pillar, output.ContentPillars)),
        };
    }
}
