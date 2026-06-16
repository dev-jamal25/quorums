using Backend.Core.Generation.Cost;
using Backend.Core.Orchestration;

namespace Backend.Infrastructure.Generation;

/// <summary>
/// Maps an agent node to the <see cref="NodeCost"/> it reports, using the P1 static estimate table
/// (DL-029) — deterministic planning numbers for the budget gate; Langfuse captures real actuals
/// separately (Phase 9). Consumes <see cref="CostEstimateTable"/> + <see cref="BudgetEvaluation"/>;
/// adds no cost model of its own.
/// </summary>
internal static class NodeCostEstimator
{
    /// <summary>The token cost a per-call LLM node reports (by its estimate-table key).</summary>
    public static NodeCost ForCall(string node, string callName, CostPrices prices)
    {
        var estimate = CostEstimateTable.Calls.First(call => string.Equals(call.Name, callName, StringComparison.Ordinal));
        var tokens = estimate.TokensPerCall * estimate.Occurrences;
        var tokenUsd = BudgetEvaluation.TokenCostUsd(
            estimate.Tier,
            estimate.InputTokens * estimate.Occurrences,
            estimate.OutputTokens * estimate.Occurrences,
            prices);
        return new NodeCost(node, tokens, tokenUsd, MediaUsd: 0m);
    }

    /// <summary>The dollar cost the Media node reports for a generated image (0 when degraded).</summary>
    public static NodeCost ForMedia(string node, decimal mediaUsd) => new(node, Tokens: 0, TokenUsd: 0m, mediaUsd);
}
