namespace Backend.Core.Generation.Cost;

/// <summary>
/// The image budget dimension (DL-029), measured as <b>dollars</b> (per-image Gemini price ×
/// image count). The pre-Media gate checks affordability on this dimension before the one paid,
/// irreversible spend; a breach degrades to a caption-only draft (DL-023, R1), it does not fail
/// the run. The Supervisor is the sole writer (R3).
/// </summary>
public sealed record MediaBudget(decimal Limit, decimal Spent)
{
    public decimal Remaining => Limit - Spent;
}
