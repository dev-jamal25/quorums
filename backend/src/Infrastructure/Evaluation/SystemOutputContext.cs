using System.Text.Json;
using Backend.Core.Evaluation;
using Microsoft.Extensions.AI.Evaluation;

namespace Backend.Infrastructure.Evaluation;

/// <summary>
/// Carries the read-only <see cref="SystemOutput"/> projection (and its <see cref="EvalCase"/>) into the
/// Microsoft.Extensions.AI.Evaluation pipeline as an <see cref="EvaluationContext"/> — the same pattern
/// the built-in evaluators use (e.g. <c>ToolCallAccuracyEvaluatorContext</c>). Our custom rule-based
/// evaluators read the strongly-typed <see cref="Output"/> via
/// <c>additionalContext.OfType&lt;SystemOutputContext&gt;()</c>; the serialized summary in
/// <see cref="EvaluationContext.Contents"/> is what the library records into the on-disk report.
/// </summary>
public sealed class SystemOutputContext : EvaluationContext
{
    public const string ContextName = "Quorums System Output";

    private static readonly JsonSerializerOptions _summaryJson =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public SystemOutput Output { get; }

    public EvalCase Case { get; }

    public SystemOutputContext(SystemOutput output, EvalCase evalCase)
        : base(ContextName, Summarize(output, evalCase))
    {
        Output = output;
        Case = evalCase;
    }

    // A compact, serializable summary for the report (the live evaluators read Output directly).
    private static string Summarize(SystemOutput output, EvalCase evalCase) =>
        JsonSerializer.Serialize(
            new
            {
                caseId = evalCase.Id,
                runId = output.RunId,
                brandId = output.BrandId,
                surface = output.TargetSurface,
                chosenIndex = output.ChosenIndex,
                budgetDegraded = output.BudgetDegraded,
                geminiCallCount = output.GeminiCallCount,
                errorCodes = output.Errors.Select(e => e.Code).ToArray(),
                fatalError = output.FatalError?.Code,
            },
            _summaryJson);
}
