using Backend.Core.Evaluation;
using Backend.Core.Generation.Cost;
using Backend.Core.Orchestration;
using Backend.Infrastructure.Evaluation;
using Backend.Infrastructure.Evaluation.Evaluators;
using Microsoft.Extensions.AI.Evaluation;
using Xunit;

namespace Backend.IntegrationTests.Eval;

/// <summary>
/// The §3 cost &amp; latency observability evaluators (DL-022/023) as custom
/// Microsoft.Extensions.AI.Evaluation <see cref="IEvaluator"/>s. Pure, deterministic, no LLM, no DB:
/// synthetic durable-record fixtures (known token counts, media count, span timings) → exact metric
/// values. No real end-to-end run is required (that would need spend). Mirrors the RankMetrics tests.
/// </summary>
[Trait("Category", "Eval")]
public sealed class CostLatencyEvaluatorTests
{
    // All four token rates equal → the blended $/token equals that rate regardless of the call mix,
    // so a synthetic cost is exact and hand-checkable.
    private static readonly CostPrices _flatRates = new(6.0m, 6.0m, 6.0m, 6.0m, GeminiPerImage: 0.04m);

    // Real-ish split rates (the production prices are config-bound; these are test literals).
    private static readonly CostPrices _splitRates = new(3.0m, 15.0m, 1.0m, 5.0m, GeminiPerImage: 0.04m);

    private static async Task<double> ValueAsync(IEvaluator evaluator, string metricName, SystemOutput output)
    {
        var (messages, response) = EvalTestData.Conversation();
        var context = new SystemOutputContext(output, EvalTestData.Case());
        var result = await evaluator.EvaluateAsync(messages, response, additionalContext: [context]);
        return result.Get<NumericMetric>(metricName).Value ?? double.NaN;
    }

    [Fact]
    public void RunCost_is_blended_token_cost_plus_media_cost()
    {
        // Flat 6 $/MTok → blended rate = 6e-6 $/token. 1,000,000 tokens × 6e-6 = $6.00; 2 images × $0.04 = $0.08.
        Assert.Equal(0.000006m, CostModel.BlendedTokenRateUsdPerToken(_flatRates));
        Assert.Equal(6.08m, CostModel.RunCostUsd(totalTokens: 1_000_000, mediaImages: 2, _flatRates));

        // Media-only term and token-only term decompose cleanly.
        Assert.Equal(0.12m, CostModel.RunCostUsd(totalTokens: 0, mediaImages: 3, _splitRates));
        Assert.Equal(
            500_000 * CostModel.BlendedTokenRateUsdPerToken(_splitRates),
            CostModel.RunCostUsd(totalTokens: 500_000, mediaImages: 0, _splitRates));

        // The split-rate blended rate is a positive usage-weighted average (Sonnet-heavy mix).
        Assert.True(CostModel.BlendedTokenRateUsdPerToken(_splitRates) > 0m);
    }

    [Fact]
    public async Task Cost_evaluator_reads_durable_tokens_and_media_count_into_a_dollar_metric()
    {
        var output = EvalTestData.ValidOutput() with
        {
            Budget = new Budget(TokenBudget: 10_000_000, TokensSpent: 1_000_000, MediaBudget: 1m, MediaSpent: 0.08m),
            GeminiCallCount = 2,
        };

        var value = await ValueAsync(new CostEvaluator(_flatRates), CostEvaluator.MetricNameConst, output);
        Assert.Equal(6.08, value, 6);
    }

    [Fact]
    public async Task Latency_evaluator_is_wall_clock_across_the_durable_spans()
    {
        var t0 = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        var output = EvalTestData.ValidOutput() with
        {
            Trace = new TraceRefs("trace-1", ["s1", "s2"],
            [
                new TraceSpan("s1", "strategy", null, "ok", t0, t0.AddMilliseconds(100), null),
                new TraceSpan("s2", "copywriting", null, "ok", t0.AddMilliseconds(150), t0.AddMilliseconds(400), null),
            ]),
        };

        // Wall-clock = max(end)=t0+400ms − min(start)=t0 = 400ms.
        var value = await ValueAsync(new LatencyEvaluator(), LatencyEvaluator.MetricNameConst, output);
        Assert.Equal(400.0, value);
    }

    [Fact]
    public async Task Latency_evaluator_is_zero_when_no_spans_were_recorded()
    {
        var output = EvalTestData.ValidOutput() with { Trace = new TraceRefs(string.Empty, [], []) };

        var value = await ValueAsync(new LatencyEvaluator(), LatencyEvaluator.MetricNameConst, output);
        Assert.Equal(0.0, value);
    }

    [Fact]
    public async Task Missing_context_reds_the_metric_with_a_diagnostic()
    {
        var (messages, response) = EvalTestData.Conversation();

        var result = await new CostEvaluator(_flatRates).EvaluateAsync(messages, response, additionalContext: []);
        var metric = result.Get<NumericMetric>(CostEvaluator.MetricNameConst);

        Assert.Equal(0.0, metric.Value);
        Assert.True(metric.Interpretation?.Failed);
    }
}
