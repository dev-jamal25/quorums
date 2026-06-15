namespace Backend.Core.Generation.Cost;

/// <summary>
/// The live prices the cost model converts token/image usage with (DL-029) — <b>config-bound</b>,
/// seeded at build/config time from current live values, never hardcoded in agent code nor recalled
/// from memory. Token prices are per <b>million</b> tokens (the natural pricing unit); the Gemini
/// price is per image. Langfuse captures actuals; Phase 9 refines the static estimates, not these.
/// </summary>
public sealed record CostPrices(
    decimal SonnetInputPerMTok,
    decimal SonnetOutputPerMTok,
    decimal HaikuInputPerMTok,
    decimal HaikuOutputPerMTok,
    decimal GeminiPerImage)
{
    /// <summary>The input price per million tokens for a tier.</summary>
    public decimal InputPerMTok(CostModelTier tier) =>
        tier == CostModelTier.Sonnet ? SonnetInputPerMTok : HaikuInputPerMTok;

    /// <summary>The output price per million tokens for a tier.</summary>
    public decimal OutputPerMTok(CostModelTier tier) =>
        tier == CostModelTier.Sonnet ? SonnetOutputPerMTok : HaikuOutputPerMTok;
}
