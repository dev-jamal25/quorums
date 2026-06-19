namespace Backend.Core.Generation.Cost;

/// <summary>
/// Maps a run's <b>durable, post-run</b> usage — the reconciled <c>Budget.TokensSpent</c> (a single
/// TOTAL token count, input+output combined, all tiers) plus the media image count — to a dollar cost,
/// for the cost observability evaluator (DL-022/023, deck S39). Pure, config-bound via
/// <see cref="CostPrices"/>; no I/O, no thresholds.
///
/// <para><b>Precision limit (deliberate).</b> The durable figure is total-tokens-only and estimate-based
/// (DL-029): the per-tier / input-output split actuals are recorded only as Langfuse generations, not in
/// the checkpoint. So token cost applies a single <b>blended</b> $/token derived from the run's expected
/// call mix (<see cref="CostEstimateTable"/> × the same config-bound prices the budget gate uses) — an
/// estimate of an estimate. It assumes the run matches that mix; Phase 9 refines it from Langfuse actuals.
/// This is observability, not a billed figure.</para>
/// </summary>
public static class CostModel
{
    /// <summary>
    /// The blended $/token = expected-case token cost ÷ expected-case token count over the frozen call
    /// mix. Equals every tier's $/token when all four rates are equal; otherwise a usage-weighted average.
    /// </summary>
    public static decimal BlendedTokenRateUsdPerToken(CostPrices prices)
    {
        ArgumentNullException.ThrowIfNull(prices);
        var tokens = CostEstimateTable.ExpectedTokenCount();
        return tokens == 0 ? 0m : CostEstimateTable.ExpectedTokenCostUsd(prices) / tokens;
    }

    /// <summary>
    /// Estimated run cost = (total durable tokens × the blended $/token) + (media images × Gemini per-image).
    /// </summary>
    public static decimal RunCostUsd(int totalTokens, int mediaImages, CostPrices prices)
    {
        ArgumentNullException.ThrowIfNull(prices);
        var tokenCost = totalTokens * BlendedTokenRateUsdPerToken(prices);
        var mediaCost = mediaImages * prices.GeminiPerImage;
        return tokenCost + mediaCost;
    }
}
