namespace Backend.Core.Evaluation;

/// <summary>
/// Cohen's κ — inter-rater agreement between two binary labellings (the LLM judge vs the human labels),
/// corrected for chance (DL-057, deck S39). κ = (p_o − p_e) / (1 − p_e), where p_o is the observed
/// agreement and p_e the agreement expected by chance from the marginals. Pure, no I/O.
///
/// <para>Interpretation guide: ≥ 0.6 substantial, ≥ 0.8 near-perfect; ~0 means no better than chance;
/// negative means systematic disagreement. The calibration gate targets κ ≥ 0.6 per axis.</para>
/// </summary>
public static class CohenKappa
{
    /// <summary>
    /// κ for two equal-length binary label arrays. Throws if the lengths differ or are empty.
    ///
    /// <para><b>Degenerate case (handled explicitly, never a divide-by-zero):</b> when chance agreement
    /// p_e = 1 — which happens only when BOTH raters assigned every item the same single label — κ is
    /// mathematically undefined. We return 1.0 when the two labellings are nonetheless identical (trivial
    /// perfect agreement) and 0.0 otherwise. A caller surfacing κ should also report n and the label
    /// balance, since a degenerate κ carries little information.</para>
    /// </summary>
    public static double Compute(IReadOnlyList<bool> rater1, IReadOnlyList<bool> rater2)
    {
        ArgumentNullException.ThrowIfNull(rater1);
        ArgumentNullException.ThrowIfNull(rater2);

        if (rater1.Count != rater2.Count)
        {
            throw new ArgumentException(
                $"label arrays must be equal length (got {rater1.Count} and {rater2.Count}).", nameof(rater2));
        }

        if (rater1.Count == 0)
        {
            throw new ArgumentException("label arrays must be non-empty.", nameof(rater1));
        }

        var n = rater1.Count;
        // 2×2 confusion counts.
        int bothTrue = 0, r1OnlyTrue = 0, r2OnlyTrue = 0, bothFalse = 0;
        for (var i = 0; i < n; i++)
        {
            switch (rater1[i], rater2[i])
            {
                case (true, true): bothTrue++; break;
                case (true, false): r1OnlyTrue++; break;
                case (false, true): r2OnlyTrue++; break;
                default: bothFalse++; break;
            }
        }

        var observed = (bothTrue + bothFalse) / (double)n;

        var r1True = (bothTrue + r1OnlyTrue) / (double)n;
        var r2True = (bothTrue + r2OnlyTrue) / (double)n;
        var chance = (r1True * r2True) + ((1 - r1True) * (1 - r2True));

        // p_e == 1 ⟺ both raters are constant on the same label ⟹ undefined κ; resolve explicitly.
        if (1 - chance <= double.Epsilon)
        {
            return observed >= 1.0 ? 1.0 : 0.0;
        }

        return (observed - chance) / (1 - chance);
    }
}
