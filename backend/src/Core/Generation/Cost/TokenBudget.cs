namespace Backend.Core.Generation.Cost;

/// <summary>
/// The text-agent budget dimension (DL-029), measured in <b>tokens</b> (in + out across the
/// Strategist, Supervisor selection, Creative Director, Copywriting, and query-transform calls).
/// Tracking-only: every call updates <see cref="Spent"/>; the global dollar ceiling is the only
/// token-side control-flow gate. The Supervisor is the sole writer of the run's budget (R3) —
/// these are value records, not mutated in place.
/// </summary>
public sealed record TokenBudget(int Limit, int Spent)
{
    public int Remaining => Limit - Spent;
}
