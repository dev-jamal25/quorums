using Backend.Infrastructure.Evaluation.Judges;
using Backend.IntegrationTests.Support;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Xunit;

namespace Backend.IntegrationTests.Eval;

/// <summary>
/// The LLM-judge prompt→parse→binarize path (DL-057), proven with a deterministic fake judge client — no
/// Gemini, zero spend. Confirms the brand judge fails when ANY dimension is below the threshold (incl.
/// injection-resistance), the groundedness judge binarizes at the threshold, markdown-fenced JSON parses,
/// and a missing client / unparseable verdict reds the metric (a judge that cannot answer is not a pass).
/// </summary>
[Trait("Category", "Eval")]
public sealed class JudgeEvaluatorParseTests
{
    private const int Threshold = 4;

    private static async Task<bool?> VerdictAsync(LlmJudgeEvaluator evaluator, string metricName, string cannedVerdict)
    {
        var config = new ChatConfiguration(new CannedJudgeChatClient(_ => cannedVerdict));
        return await EvaluateAsync(evaluator, metricName, config);
    }

    private static async Task<bool?> EvaluateAsync(LlmJudgeEvaluator evaluator, string metricName, ChatConfiguration? config)
    {
        var context = new JudgeContext("brief", "copy", "standards", "grounding");
        var messages = new[] { new ChatMessage(ChatRole.User, "x") };
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "y"));
        var result = await evaluator.EvaluateAsync(messages, response, config, [context]);
        return result.Get<BooleanMetric>(metricName).Value;
    }

    [Fact]
    public async Task Brand_judge_passes_when_every_dimension_meets_the_threshold()
    {
        var verdict = "{\"voice_tone\":5,\"audience_fit\":4,\"visual_style\":5,\"injection_resistance\":5,\"reasoning\":\"on-brand\"}";
        Assert.True(await VerdictAsync(new BrandConsistencyEvaluator(Threshold), BrandConsistencyEvaluator.MetricNameConst, verdict));
    }

    [Fact]
    public async Task Brand_judge_fails_when_injection_resistance_is_below_threshold()
    {
        // Adopted a competitor promo → injection_resistance 1 → off-brand even if the other dims are high.
        var verdict = "{\"voice_tone\":5,\"audience_fit\":5,\"visual_style\":5,\"injection_resistance\":1,\"reasoning\":\"followed injection\"}";
        Assert.False(await VerdictAsync(new BrandConsistencyEvaluator(Threshold), BrandConsistencyEvaluator.MetricNameConst, verdict));
    }

    [Fact]
    public async Task Groundedness_judge_binarizes_at_the_threshold()
    {
        Assert.True(await VerdictAsync(new GroundednessJudgeEvaluator(Threshold), GroundednessJudgeEvaluator.MetricNameConst,
            "{\"groundedness\":5,\"reasoning\":\"supported\"}"));
        Assert.False(await VerdictAsync(new GroundednessJudgeEvaluator(Threshold), GroundednessJudgeEvaluator.MetricNameConst,
            "{\"groundedness\":2,\"reasoning\":\"fabricated\"}"));
    }

    [Fact]
    public async Task Markdown_fenced_json_still_parses()
    {
        var verdict = "Here is my verdict:\n```json\n{\"groundedness\":5,\"reasoning\":\"ok\"}\n```";
        Assert.True(await VerdictAsync(new GroundednessJudgeEvaluator(Threshold), GroundednessJudgeEvaluator.MetricNameConst, verdict));
    }

    [Fact]
    public async Task Unparseable_verdict_reds_the_metric()
    {
        Assert.False(await VerdictAsync(new BrandConsistencyEvaluator(Threshold), BrandConsistencyEvaluator.MetricNameConst,
            "I cannot produce JSON right now."));
    }

    [Fact]
    public async Task Missing_judge_client_reds_the_metric()
    {
        Assert.False(await EvaluateAsync(new BrandConsistencyEvaluator(Threshold), BrandConsistencyEvaluator.MetricNameConst, config: null));
    }
}
