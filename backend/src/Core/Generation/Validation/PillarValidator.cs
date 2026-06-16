namespace Backend.Core.Generation.Validation;

/// <summary>The three outcomes of checking a strategy's pillar against the brand's pillar list (R7).</summary>
public enum PillarStatus
{
    /// <summary>The pillar is one of the brand's structured pillars — accept.</summary>
    InList,

    /// <summary>The pillar is outside the brand's list — a schema-level violation → regenerate (DL-026).</summary>
    NotInList,

    /// <summary>The brand exposes no structured pillars — the caller logs "skipped", never a silent pass (R7).</summary>
    NoPillarsDefined,
}

/// <summary>
/// Validates the Content Strategist's free-string <c>pillar</c> against the brand's structured
/// pillar list at receipt (DL-026, DL-034 R7). Pure: the caller (a node) maps <see cref="PillarStatus"/>
/// to a regenerate (<see cref="PillarStatus.NotInList"/>) or an explicit "no structured pillars —
/// skipped" log (<see cref="PillarStatus.NoPillarsDefined"/>) — the skip is an observable state, so
/// it is never a silent pass. Loading the brand's list under RLS is node behaviour.
/// </summary>
public static class PillarValidator
{
    public static PillarStatus Check(string? pillar, IReadOnlyList<string>? brandPillars)
    {
        if (brandPillars is null || brandPillars.Count == 0)
        {
            return PillarStatus.NoPillarsDefined;
        }

        var needle = pillar?.Trim() ?? string.Empty;
        var hit = brandPillars.Any(candidate =>
            string.Equals(candidate?.Trim(), needle, StringComparison.OrdinalIgnoreCase));

        return hit ? PillarStatus.InList : PillarStatus.NotInList;
    }

    /// <summary>The concrete, feed-back-able error for a <see cref="PillarStatus.NotInList"/> miss.</summary>
    public static string DescribeMiss(string? pillar, IReadOnlyList<string> brandPillars) =>
        $"pillar '{pillar}' is not one of the brand's pillars [{string.Join(", ", brandPillars)}]";
}
