using Backend.Core.Evaluation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace Backend.Infrastructure.Evaluation.Evaluators;

/// <summary>
/// Base for the deterministic, **no-LLM** numeric observability evaluators over a generation run
/// (evaluators.md §3 — cost &amp; latency). Mirrors <see cref="SystemOutputEvaluator"/> but emits a graded
/// <see cref="NumericMetric"/> rather than a pass/fail: these are **tracked, not merge-blocking** — no
/// threshold, no interpretation. Reads the run from the <see cref="SystemOutputContext"/>; tolerates a null
/// <c>chatConfiguration</c>.
/// </summary>
public abstract class SystemOutputNumericEvaluator : IEvaluator
{
    protected abstract string MetricName { get; }

    public IReadOnlyCollection<string> EvaluationMetricNames => [MetricName];

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        var metric = new NumericMetric(MetricName);

        if (additionalContext?.OfType<SystemOutputContext>().FirstOrDefault() is not { } context)
        {
            metric.AddDiagnostics(EvaluationDiagnostic.Error(
                $"A value of type {nameof(SystemOutputContext)} was not found in the {nameof(additionalContext)} collection."));
            metric.Value = 0.0;
            metric.Interpretation = new EvaluationMetricInterpretation(failed: true, reason: "missing SystemOutputContext");
            return new ValueTask<EvaluationResult>(new EvaluationResult(metric));
        }

        var (value, reason) = Compute(context.Output, context.Case);
        metric.Value = value;
        metric.Reason = reason;
        return new ValueTask<EvaluationResult>(new EvaluationResult(metric));
    }

    /// <summary>Computes the numeric observability value (and a human-readable reason) for the run.</summary>
    protected abstract (double Value, string Reason) Compute(SystemOutput output, EvalCase evalCase);
}
