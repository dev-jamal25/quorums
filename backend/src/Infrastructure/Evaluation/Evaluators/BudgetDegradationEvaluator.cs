using Backend.Core.Evaluation;

namespace Backend.Infrastructure.Evaluation.Evaluators;

/// <summary>
/// §1 Budget-degradation invariant (DL-023/034) — *never overspend, never crash, never fail silently*.
/// When the pre-Media gate degrades (media unaffordable), the run MUST produce a valid caption-only
/// draft, record a <c>BudgetDegraded</c> event, make <b>zero Gemini calls</b>, and not fail; Copywriting
/// is unaffected (the caption still exists). When not degraded, the invariant holds vacuously.
/// </summary>
public sealed class BudgetDegradationEvaluator : SystemOutputEvaluator
{
    public const string MetricNameConst = "Budget Degradation Invariant";

    protected override string MetricName => MetricNameConst;

    protected override Verdict Evaluate(SystemOutput output, EvalCase evalCase)
    {
        if (!output.BudgetDegraded)
        {
            return Verdict.Pass("run did not degrade; invariant holds vacuously");
        }

        if (output.GeminiCallCount != 0)
        {
            return Verdict.Fail($"degraded run made {output.GeminiCallCount} Gemini call(s); must be zero");
        }

        if (output.FatalError is { } fatal)
        {
            return Verdict.Fail($"degraded run must not fail, but FatalError '{fatal.Code}' is set");
        }

        var mediaPresent = output.Media is not null || output.Draft?.MediaRef is not null;
        if (mediaPresent)
        {
            return Verdict.Fail("degraded run must be caption-only, but a media asset is present");
        }

        if (output.Caption is null)
        {
            return Verdict.Fail("degraded run must keep a valid caption (Copywriting unaffected), but Caption is null");
        }

        return Verdict.Pass("degraded to a valid caption-only draft with zero Gemini calls and no fatal error");
    }
}
