namespace Backend.Core.Generation.Cost;

/// <summary>
/// The static per-call cost estimate table (DL-029) plus the expected/worst-case rollups derived
/// from it. Planning numbers only — they seed provisioning; Langfuse actuals refine them in Phase 9.
/// The Strategist's N = 3 candidate fan-out is baked into its output estimate; the query-transform
/// occurs per retrieving agent × variants. Worst case = the full fan-out with every retryable agent
/// exhausting its bounded retries.
/// </summary>
public static class CostEstimateTable
{
    /// <summary>N = 3 candidate fan-out (DL-027).</summary>
    public const int CandidateCount = 3;

    /// <summary>The bounded retry budget per retryable agent (DL-027/028).</summary>
    public const int MaxRetries = 2;

    /// <summary>
    /// The frozen planning estimates. Embed/rerank are local (TEI) → $0 and are not token calls.
    /// </summary>
    public static IReadOnlyList<CallEstimate> Calls { get; } =
    [
        new("content_strategist",   CostModelTier.Sonnet, InputTokens: 3000, OutputTokens: 1800, Occurrences: 1, Retryable: true),
        new("supervisor_selection", CostModelTier.Sonnet, InputTokens: 1000, OutputTokens: 150,  Occurrences: 1, Retryable: true),
        new("creative_director",    CostModelTier.Sonnet, InputTokens: 3000, OutputTokens: 450,  Occurrences: 1, Retryable: true),
        new("copywriting",          CostModelTier.Haiku,  InputTokens: 3000, OutputTokens: 250,  Occurrences: 1, Retryable: true),
        new("query_transform",      CostModelTier.Haiku,  InputTokens: 250,  OutputTokens: 120,  Occurrences: 9, Retryable: false),
    ];

    /// <summary>Expected-case total token count for one run (no retries).</summary>
    public static int ExpectedTokenCount() =>
        Calls.Sum(call => call.TokensPerCall * call.Occurrences);

    /// <summary>Worst-case total token count: retryable calls run (1 + <see cref="MaxRetries"/>) times.</summary>
    public static int WorstCaseTokenCount() =>
        Calls.Sum(call => call.TokensPerCall * call.Occurrences * RetryFactor(call));

    /// <summary>Expected-case token cost in dollars.</summary>
    public static decimal ExpectedTokenCostUsd(CostPrices prices)
    {
        ArgumentNullException.ThrowIfNull(prices);
        return Calls.Sum(call => CallCostUsd(call, prices));
    }

    /// <summary>Worst-case token cost in dollars (retryable calls multiplied).</summary>
    public static decimal WorstCaseTokenCostUsd(CostPrices prices)
    {
        ArgumentNullException.ThrowIfNull(prices);
        return Calls.Sum(call => CallCostUsd(call, prices) * RetryFactor(call));
    }

    private static int RetryFactor(CallEstimate call) => call.Retryable ? 1 + MaxRetries : 1;

    private static decimal CallCostUsd(CallEstimate call, CostPrices prices) =>
        call.Occurrences * (
            (call.InputTokens / 1_000_000m * prices.InputPerMTok(call.Tier)) +
            (call.OutputTokens / 1_000_000m * prices.OutputPerMTok(call.Tier)));
}
