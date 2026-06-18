using Backend.Core.Evaluation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace Backend.Infrastructure.Evaluation.Evaluators;

/// <summary>
/// Base for the deterministic, **no-LLM** rule-based evaluators (evaluators.md §1). Each implements the
/// Microsoft.Extensions.AI.Evaluation <see cref="IEvaluator"/> so it plugs into the same pipeline +
/// reporting as the built-ins, but reads the run from the <see cref="SystemOutputContext"/> (no
/// <c>chatConfiguration</c> required) and emits a single boolean metric. Subclasses delegate the actual
/// rule to the existing Core validators — they never re-implement a contract check.
/// </summary>
public abstract class SystemOutputEvaluator : IEvaluator
{
    /// <summary>The single metric this evaluator emits (also the stored <c>eval_result.evaluator_name</c>).</summary>
    protected abstract string MetricName { get; }

    public IReadOnlyCollection<string> EvaluationMetricNames => [MetricName];

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        var metric = new BooleanMetric(MetricName);

        if (additionalContext?.OfType<SystemOutputContext>().FirstOrDefault() is not { } context)
        {
            metric.AddDiagnostics(EvaluationDiagnostic.Error(
                $"A value of type {nameof(SystemOutputContext)} was not found in the {nameof(additionalContext)} collection."));
            metric.Value = false;
            metric.Interpretation = new EvaluationMetricInterpretation(failed: true, reason: "missing SystemOutputContext");
            return new ValueTask<EvaluationResult>(new EvaluationResult(metric));
        }

        var verdict = Evaluate(context.Output, context.Case);
        metric.Value = verdict.Passed;
        metric.Reason = verdict.Reason;
        metric.Interpretation = new EvaluationMetricInterpretation(failed: !verdict.Passed, reason: verdict.Reason);

        return new ValueTask<EvaluationResult>(new EvaluationResult(metric));
    }

    /// <summary>Applies the rule to the projected run; returns pass/fail + a human-readable reason.</summary>
    protected abstract Verdict Evaluate(SystemOutput output, EvalCase evalCase);

    protected readonly record struct Verdict(bool Passed, string Reason)
    {
        public static Verdict Pass(string reason) => new(true, reason);

        public static Verdict Fail(string reason) => new(false, reason);
    }
}
