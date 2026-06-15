using Backend.Core.Generation.Cost;
using Xunit;

namespace Backend.UnitTests.Generation;

/// <summary>
/// The pure cost-model functions (DL-029): the two enforcement checks (media affordability + the
/// global ceiling as a fork-time snapshot, R2), provisioning (budget = expected × 1.5; ceiling =
/// worst-case), the per-call dollar conversion, and the estimate-table rollups. No node wiring.
/// </summary>
public sealed class CostModelTests
{
    // Current live-ish values (the production prices are config-bound; these are test literals).
    private static readonly CostPrices _prices = new(
        SonnetInputPerMTok: 3.0m,
        SonnetOutputPerMTok: 15.0m,
        HaikuInputPerMTok: 1.0m,
        HaikuOutputPerMTok: 5.0m,
        GeminiPerImage: 0.04m);

    [Fact]
    public void CanAffordMedia_is_true_up_to_the_limit_and_false_beyond_it()
    {
        var media = new MediaBudget(Limit: 0.06m, Spent: 0m);

        Assert.True(BudgetEvaluation.CanAffordMedia(media, perImagePrice: 0.04m, imageCount: 1));   // 0.04 <= 0.06
        Assert.False(BudgetEvaluation.CanAffordMedia(media, perImagePrice: 0.04m, imageCount: 2));  // 0.08 > 0.06
    }

    [Fact]
    public void CanAffordMedia_accounts_for_already_spent()
    {
        var media = new MediaBudget(Limit: 0.06m, Spent: 0.04m);

        Assert.False(BudgetEvaluation.CanAffordMedia(media, perImagePrice: 0.04m, imageCount: 1)); // 0.08 > 0.06
    }

    [Theory]
    [InlineData(1.0, 0.5, true)]
    [InlineData(0.4, 0.5, false)]
    [InlineData(0.5, 0.5, false)]  // exactly at the ceiling is not "grossly exceeds"
    public void ExceedsCeiling_compares_the_fork_time_snapshot(double snapshot, double ceiling, bool expected)
    {
        Assert.Equal(expected, BudgetEvaluation.ExceedsCeiling((decimal)snapshot, (decimal)ceiling));
    }

    [Fact]
    public void ProvisionTokenBudget_is_expected_token_count_times_the_safety_margin()
    {
        var expected = (int)System.Math.Ceiling(CostEstimateTable.ExpectedTokenCount() * BudgetEvaluation.SafetyMargin);

        Assert.Equal(expected, BudgetEvaluation.ProvisionTokenBudget().Limit);
    }

    [Fact]
    public void ProvisionMediaBudget_is_expected_image_spend_times_the_safety_margin()
    {
        // 0.04 × 1 image × 1.5 = 0.06
        Assert.Equal(0.06m, BudgetEvaluation.ProvisionMediaBudget(_prices).Limit);
    }

    [Fact]
    public void WorstCase_costs_exceed_expected_costs_and_the_ceiling_includes_media()
    {
        var expectedTokens = CostEstimateTable.ExpectedTokenCostUsd(_prices);
        var worstTokens = CostEstimateTable.WorstCaseTokenCostUsd(_prices);
        var ceiling = BudgetEvaluation.WorstCaseCeilingUsd(_prices);

        Assert.True(worstTokens > expectedTokens);               // retries multiply the retryable calls
        Assert.True(CostEstimateTable.WorstCaseTokenCount() > CostEstimateTable.ExpectedTokenCount());
        Assert.Equal(worstTokens + _prices.GeminiPerImage, ceiling); // ceiling adds one worst-case image
    }

    [Fact]
    public void TokenCostUsd_converts_per_million_token_prices()
    {
        // 1,000,000 input tokens at $3/MTok = $3.00; 200,000 output at $15/MTok = $3.00 → $6.00
        var cost = BudgetEvaluation.TokenCostUsd(CostModelTier.Sonnet, inputTokens: 1_000_000, outputTokens: 200_000, _prices);

        Assert.Equal(6.0m, cost);
    }
}
