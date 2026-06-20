namespace Backend.Core.Generation.Cost;

/// <summary>
/// Pure budget-evaluation functions (DL-029). These are the two enforcement points — media
/// affordability (the pre-Media gate) and the global per-run dollar ceiling — plus provisioning
/// (budget = expected × 1.5; ceiling = worst-case) and a tracking helper. No control flow, no I/O:
/// the node wiring that calls these (the Media gate, the Supervisor reconciliation) is the next
/// prompt. The global-ceiling check takes a fork-time snapshot value (R2); it does not reconcile.
/// </summary>
public static class BudgetEvaluation
{
    /// <summary>Provisioning safety margin: budget = expected-case × this (DL-029).</summary>
    public const decimal SafetyMargin = 1.5m;

    /// <summary>Expected number of media images per run (one asset).</summary>
    public const int ExpectedImagesPerRun = 1;

    /// <summary>Worst-case media images per run (the run produces at most one asset).</summary>
    public const int WorstCaseImagesPerRun = 1;

    // -- Enforcement point 1: pre-Media gate (media affordability) ------------------------------

    /// <summary>Whether the next <paramref name="imageCount"/> images are affordable under the media budget.</summary>
    public static bool CanAffordMedia(MediaBudget media, decimal perImagePrice, int imageCount)
    {
        ArgumentNullException.ThrowIfNull(media);
        var projected = media.Spent + (perImagePrice * imageCount);
        return projected <= media.Limit;
    }

    /// <summary>
    /// Whether one Veo clip is affordable under the media budget (DL-058): video price-per-second ×
    /// duration, plus the one Nano-Banana image when the source is image-seed. Mirrors
    /// <see cref="CanAffordMedia"/> — the pre-Media gate degrades to caption-only when this is false,
    /// before any paid Veo job is submitted.
    /// </summary>
    public static bool CanAffordVideo(
        MediaBudget media, decimal videoPricePerSec, int durationSec, bool includeSeedImage, decimal perImagePrice)
    {
        ArgumentNullException.ThrowIfNull(media);
        var projected = media.Spent + VideoCostUsd(videoPricePerSec, durationSec, includeSeedImage, perImagePrice);
        return projected <= media.Limit;
    }

    /// <summary>The dollar cost of one Veo clip (price/sec × duration) plus the seed image when image-seeded.</summary>
    public static decimal VideoCostUsd(
        decimal videoPricePerSec, int durationSec, bool includeSeedImage, decimal perImagePrice) =>
        (videoPricePerSec * durationSec) + (includeSeedImage ? perImagePrice : 0m);

    // -- Enforcement point 2: global per-run dollar ceiling (fork-time snapshot, R2) -------------

    /// <summary>Whether a fork-time combined-spend snapshot grossly exceeds the run's hard ceiling.</summary>
    public static bool ExceedsCeiling(decimal combinedSpentSnapshot, decimal ceiling) =>
        combinedSpentSnapshot > ceiling;

    // -- Provisioning: budget = expected × 1.5; ceiling = worst-case ----------------------------

    /// <summary>Provisions the token budget at expected-case token count × <see cref="SafetyMargin"/>.</summary>
    public static TokenBudget ProvisionTokenBudget()
    {
        var limit = (int)Math.Ceiling(CostEstimateTable.ExpectedTokenCount() * SafetyMargin);
        return new TokenBudget(Limit: limit, Spent: 0);
    }

    /// <summary>Provisions the media budget (dollars) at expected image spend × <see cref="SafetyMargin"/>.</summary>
    public static MediaBudget ProvisionMediaBudget(CostPrices prices)
    {
        ArgumentNullException.ThrowIfNull(prices);
        var limit = prices.GeminiPerImage * ExpectedImagesPerRun * SafetyMargin;
        return new MediaBudget(Limit: limit, Spent: 0);
    }

    /// <summary>The global hard ceiling (dollars) at worst-case token cost + worst-case media cost.</summary>
    public static decimal WorstCaseCeilingUsd(CostPrices prices)
    {
        ArgumentNullException.ThrowIfNull(prices);
        return CostEstimateTable.WorstCaseTokenCostUsd(prices)
             + (prices.GeminiPerImage * WorstCaseImagesPerRun);
    }

    // -- Tracking helper: dollar cost of an actual token usage for a tier -----------------------

    /// <summary>The dollar cost of an actual token usage for a tier (used to update Spent everywhere).</summary>
    public static decimal TokenCostUsd(CostModelTier tier, int inputTokens, int outputTokens, CostPrices prices)
    {
        ArgumentNullException.ThrowIfNull(prices);
        return (inputTokens / 1_000_000m * prices.InputPerMTok(tier))
             + (outputTokens / 1_000_000m * prices.OutputPerMTok(tier));
    }
}
