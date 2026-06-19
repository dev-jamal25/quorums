using Backend.Core.Evaluation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace Backend.Infrastructure.Evaluation.Evaluators;

/// <summary>
/// Base for the deterministic, **no-LLM** reference-based retrieval rank evaluators (evaluators.md §2).
/// Each implements the Microsoft.Extensions.AI.Evaluation <see cref="IEvaluator"/> so it plugs into the
/// same pipeline + reporting as the rule-based and built-in evaluators, but reads the ranked result + the
/// golden <c>R_q</c> from the <see cref="RetrievalEvalContext"/> (no <c>chatConfiguration</c> required)
/// and emits a single graded <see cref="NumericMetric"/>. Subclasses delegate the math to the pure
/// <see cref="RankMetrics"/> — they never re-implement a formula.
/// </summary>
public abstract class RetrievalRankEvaluator : IEvaluator
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
        var metric = new NumericMetric(MetricName);

        if (additionalContext?.OfType<RetrievalEvalContext>().FirstOrDefault() is not { } context)
        {
            metric.AddDiagnostics(EvaluationDiagnostic.Error(
                $"A value of type {nameof(RetrievalEvalContext)} was not found in the {nameof(additionalContext)} collection."));
            metric.Value = 0.0;
            metric.Interpretation = new EvaluationMetricInterpretation(failed: true, reason: "missing RetrievalEvalContext");
            return new ValueTask<EvaluationResult>(new EvaluationResult(metric));
        }

        var value = Compute(context.RankedChunkIds, context.RelevantChunkIds);
        metric.Value = value;
        metric.Reason = $"{MetricName} = {value:0.###} for '{context.CaseId}' " +
            $"({context.RankedChunkIds.Count} retrieved vs {context.RelevantChunkIds.Count} relevant).";

        return new ValueTask<EvaluationResult>(new EvaluationResult(metric));
    }

    /// <summary>Computes the per-query metric value from the ranked list and the golden relevant set.</summary>
    protected abstract double Compute(IReadOnlyList<Guid> ranked, IReadOnlyCollection<Guid> relevant);
}

/// <summary>Context recall @ k (k = 1, 3 only at this corpus scale — evaluators.md §2).</summary>
public sealed class ContextRecallEvaluator : RetrievalRankEvaluator
{
    private readonly int _k;

    public ContextRecallEvaluator(int k) => _k = k;

    /// <summary>The metric name for a given k (e.g. <c>Context Recall@1</c>).</summary>
    public static string Name(int k) => $"Context Recall@{k}";

    protected override string MetricName => Name(_k);

    protected override double Compute(IReadOnlyList<Guid> ranked, IReadOnlyCollection<Guid> relevant) =>
        RankMetrics.Recall(ranked, relevant, _k);
}

/// <summary>Hit@1 — was the top-ranked chunk relevant.</summary>
public sealed class HitAtOneEvaluator : RetrievalRankEvaluator
{
    public const string MetricNameConst = "Hit@1";

    protected override string MetricName => MetricNameConst;

    protected override double Compute(IReadOnlyList<Guid> ranked, IReadOnlyCollection<Guid> relevant) =>
        RankMetrics.HitAtOne(ranked, relevant);
}

/// <summary>Reciprocal rank (mean over queries = MRR) — rank-sensitive, exercises the reranker.</summary>
public sealed class ReciprocalRankEvaluator : RetrievalRankEvaluator
{
    public const string MetricNameConst = "MRR";

    protected override string MetricName => MetricNameConst;

    protected override double Compute(IReadOnlyList<Guid> ranked, IReadOnlyCollection<Guid> relevant) =>
        RankMetrics.ReciprocalRank(ranked, relevant);
}

/// <summary>Rank-aware context precision — the primary stage-discriminating metric.</summary>
public sealed class ContextPrecisionEvaluator : RetrievalRankEvaluator
{
    public const string MetricNameConst = "Context Precision";

    protected override string MetricName => MetricNameConst;

    // The ranked list is already the retrieved top-k cut; precision is averaged over that whole list.
    protected override double Compute(IReadOnlyList<Guid> ranked, IReadOnlyCollection<Guid> relevant) =>
        RankMetrics.ContextPrecision(ranked, relevant, ranked.Count);
}
