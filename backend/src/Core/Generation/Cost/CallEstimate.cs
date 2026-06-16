namespace Backend.Core.Generation.Cost;

/// <summary>
/// One row of the static cost estimate table (DL-029): the planning in/out token estimate for a
/// per-run LLM call. <see cref="Occurrences"/> is how many times the call happens in the expected
/// case (e.g. the query-transform runs per retrieving agent × variants); <see cref="Retryable"/>
/// marks calls whose cost multiplies under the bounded retry loop in the worst case. The Strategist's
/// N = 3 candidate fan-out is baked into its output-token estimate. These are planning numbers —
/// Langfuse actuals refine them in Phase 9.
/// </summary>
public sealed record CallEstimate(
    string Name,
    CostModelTier Tier,
    int InputTokens,
    int OutputTokens,
    int Occurrences,
    bool Retryable)
{
    /// <summary>Total tokens (in + out) for one occurrence of this call.</summary>
    public int TokensPerCall => InputTokens + OutputTokens;
}
