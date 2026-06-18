namespace Backend.Core.Orchestration.Contracts;

/// <summary>
/// Locates a selected <see cref="ContentStrategy"/> within the banked N=3 candidates (DL-027). The
/// match is by the distinguishing fields (<see cref="ContentStrategy.Pillar"/>,
/// <see cref="ContentStrategy.Angle"/>, <see cref="ContentStrategy.Objective"/>) — deliberately NOT
/// record <c>==</c>: <see cref="ContentStrategy"/>'s synthesized equality recurses into
/// <see cref="Grounding"/>, whose <c>ChunkIdsUsed</c> list compares by reference, so two value-identical
/// strategies that have crossed the <c>RunCheckpoint</c> JSON round-trip are never <c>==</c>. Both the
/// reselect-angle rewind and the review projection key off the chosen index, so the comparison lives in
/// exactly one place.
/// </summary>
public static class StrategySelection
{
    /// <summary>The index of <paramref name="selected"/> in <paramref name="candidates"/>, or -1 if absent.</summary>
    public static int IndexOf(IReadOnlyList<ContentStrategy>? candidates, ContentStrategy? selected)
    {
        if (candidates is null || selected is null)
        {
            return -1;
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            if (Matches(candidates[i], selected))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Value-equality on the distinguishing fields only (see the type remarks).</summary>
    public static bool Matches(ContentStrategy a, ContentStrategy b) =>
        string.Equals(a.Pillar, b.Pillar, StringComparison.Ordinal)
        && string.Equals(a.Angle, b.Angle, StringComparison.Ordinal)
        && a.Objective == b.Objective;
}
