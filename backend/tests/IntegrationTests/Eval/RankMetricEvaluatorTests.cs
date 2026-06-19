using Backend.Infrastructure.Evaluation;
using Backend.Infrastructure.Evaluation.Evaluators;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Xunit;

namespace Backend.IntegrationTests.Eval;

/// <summary>
/// The reference-based retrieval rank evaluators (evaluators.md §2, DL-048) as custom
/// Microsoft.Extensions.AI.Evaluation <see cref="IEvaluator"/>s. Known rankings → known values prove both
/// correctness and *discriminating power* (a distractor at the top drops context precision) before any
/// pipeline runs. Pure, deterministic, no LLM, no DB: zero API spend.
/// </summary>
[Trait("Category", "Eval")]
public sealed class RankMetricEvaluatorTests
{
    private static readonly Guid _a = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _b = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid _c = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid _distractor = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private static async Task<double> ValueAsync(
        IEvaluator evaluator, string metricName, IReadOnlyList<Guid> ranked, IReadOnlyCollection<Guid> relevant)
    {
        var context = new RetrievalEvalContext("GR-unit", "a unit query", ranked, relevant);
        var messages = new[] { new ChatMessage(ChatRole.User, "retrieve") };
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ranked"));
        var result = await evaluator.EvaluateAsync(messages, response, additionalContext: [context]);
        return result.Get<NumericMetric>(metricName).Value ?? double.NaN;
    }

    [Fact]
    public async Task Relevant_at_rank_1_scores_perfectly_across_every_metric()
    {
        var ranked = new[] { _a, _b, _c };
        var relevant = new[] { _a };

        Assert.Equal(1.0, await ValueAsync(new HitAtOneEvaluator(), HitAtOneEvaluator.MetricNameConst, ranked, relevant));
        Assert.Equal(1.0, await ValueAsync(new ReciprocalRankEvaluator(), ReciprocalRankEvaluator.MetricNameConst, ranked, relevant));
        Assert.Equal(1.0, await ValueAsync(new ContextPrecisionEvaluator(), ContextPrecisionEvaluator.MetricNameConst, ranked, relevant));
        Assert.Equal(1.0, await ValueAsync(new ContextRecallEvaluator(1), ContextRecallEvaluator.Name(1), ranked, relevant));
        Assert.Equal(1.0, await ValueAsync(new ContextRecallEvaluator(3), ContextRecallEvaluator.Name(3), ranked, relevant));
    }

    [Fact]
    public async Task Relevant_at_rank_3_misses_hit_at_1_and_yields_one_third_rank_score()
    {
        var ranked = new[] { _distractor, _b, _a };  // the single relevant id A sits at rank 3
        var relevant = new[] { _a };

        Assert.Equal(0.0, await ValueAsync(new HitAtOneEvaluator(), HitAtOneEvaluator.MetricNameConst, ranked, relevant));
        Assert.Equal(0.333, await ValueAsync(new ReciprocalRankEvaluator(), ReciprocalRankEvaluator.MetricNameConst, ranked, relevant), 3);
        // Average precision @ k with the only relevant doc at rank 3 = (1/3) / 1 = 0.333.
        Assert.Equal(0.333, await ValueAsync(new ContextPrecisionEvaluator(), ContextPrecisionEvaluator.MetricNameConst, ranked, relevant), 3);
        Assert.Equal(0.0, await ValueAsync(new ContextRecallEvaluator(1), ContextRecallEvaluator.Name(1), ranked, relevant));
        Assert.Equal(1.0, await ValueAsync(new ContextRecallEvaluator(3), ContextRecallEvaluator.Name(3), ranked, relevant));
    }

    [Fact]
    public async Task A_distractor_at_rank_1_drops_context_precision_below_perfect_ranking()
    {
        var relevant = new[] { _a, _b };

        var perfect = await ValueAsync(
            new ContextPrecisionEvaluator(), ContextPrecisionEvaluator.MetricNameConst, new[] { _a, _b }, relevant);
        var distractorFirst = await ValueAsync(
            new ContextPrecisionEvaluator(), ContextPrecisionEvaluator.MetricNameConst, new[] { _distractor, _a, _b }, relevant);

        Assert.Equal(1.0, perfect);
        Assert.True(distractorFirst < perfect, $"distractor-at-top precision {distractorFirst:0.###} should drop below {perfect:0.###}");
        // (1/2 + 2/3) / 2 = 0.583 — the rank-aware metric penalizes the misplaced relevant docs.
        Assert.Equal(0.583, distractorFirst, 3);

        // Hit@1 / MRR also collapse when the top result is a distractor.
        Assert.Equal(0.0, await ValueAsync(new HitAtOneEvaluator(), HitAtOneEvaluator.MetricNameConst, new[] { _distractor, _a, _b }, relevant));
        Assert.Equal(0.5, await ValueAsync(new ReciprocalRankEvaluator(), ReciprocalRankEvaluator.MetricNameConst, new[] { _distractor, _a, _b }, relevant));
    }

    [Fact]
    public async Task Multi_member_relevant_set_recall_counts_intersection_at_k()
    {
        var ranked = new[] { _a, _distractor, _b, _c };  // 2 of the 2 relevant ids (A, B) within top-3
        var relevant = new[] { _a, _b };

        Assert.Equal(0.5, await ValueAsync(new ContextRecallEvaluator(1), ContextRecallEvaluator.Name(1), ranked, relevant)); // only A in top-1
        Assert.Equal(1.0, await ValueAsync(new ContextRecallEvaluator(3), ContextRecallEvaluator.Name(3), ranked, relevant)); // A and B in top-3
    }

    [Fact]
    public async Task Missing_context_reds_the_metric_with_a_diagnostic()
    {
        var messages = new[] { new ChatMessage(ChatRole.User, "retrieve") };
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "ranked"));

        var result = await new ContextPrecisionEvaluator().EvaluateAsync(messages, response, additionalContext: []);
        var metric = result.Get<NumericMetric>(ContextPrecisionEvaluator.MetricNameConst);

        Assert.Equal(0.0, metric.Value);
        Assert.True(metric.Interpretation?.Failed);
    }
}
