using Backend.Core.Evaluation;

namespace Backend.Infrastructure.Evaluation.Evaluators;

/// <summary>
/// §1.1 Schema validity — each agent output is present and fully typed: the N=3 strategy candidates, the
/// chosen <c>ContentStrategy</c>, the <c>CreativeDirection</c> with a structured <c>MediaPromptBrief</c>,
/// and the <c>Caption</c>, each carrying its <c>Grounding</c>; and no terminal schema-violation
/// <c>ToolError</c>. A forced tool guarantees shape, not truth — so shape is checked here with rules.
/// </summary>
public sealed class SchemaValidityEvaluator : SystemOutputEvaluator
{
    public const string MetricNameConst = "Schema Validity";

    protected override string MetricName => MetricNameConst;

    protected override Verdict Evaluate(SystemOutput output, EvalCase evalCase)
    {
        if (output.Errors.Any(e => e.Code == GenerationErrorCodes.SchemaViolation)
            || output.FatalError?.Code == GenerationErrorCodes.SchemaViolation)
        {
            return Verdict.Fail("a terminal generation.schema_violation ToolError is present");
        }

        var candidateCount = output.Candidates?.Candidates.Count ?? 0;
        if (candidateCount != 3)
        {
            return Verdict.Fail($"expected 3 strategy candidates, found {candidateCount}");
        }

        if (output.Strategy is not { } strategy || strategy.Grounding is null)
        {
            return Verdict.Fail("the chosen ContentStrategy or its Grounding is missing");
        }

        if (output.Creative is not { } creative || creative.MediaPromptBrief is null || creative.Grounding is null)
        {
            return Verdict.Fail("the CreativeDirection, its MediaPromptBrief, or its Grounding is missing");
        }

        if (output.Caption is not { } caption || caption.Grounding is null)
        {
            return Verdict.Fail("the Caption or its Grounding is missing");
        }

        if (output.Candidates!.Candidates.Any(c => c.Grounding is null))
        {
            return Verdict.Fail("a strategy candidate is missing its Grounding");
        }

        return Verdict.Pass("all typed agent outputs are present with required fields and grounding");
    }
}
