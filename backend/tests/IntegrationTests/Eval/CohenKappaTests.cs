using Backend.Core.Evaluation;
using Xunit;

namespace Backend.IntegrationTests.Eval;

/// <summary>
/// Cohen's κ (DL-057): chance-corrected inter-rater agreement, the calibration gate's statistic. Pure,
/// synthetic, no LLM, no DB — known label arrays → known κ, including the all-same degenerate case.
/// </summary>
[Trait("Category", "Eval")]
public sealed class CohenKappaTests
{
    [Fact]
    public void Perfect_agreement_is_one()
    {
        var a = new[] { true, false, true, true, false };
        Assert.Equal(1.0, CohenKappa.Compute(a, a));
    }

    [Fact]
    public void Independent_labellings_with_equal_marginals_are_about_zero()
    {
        // a=1, b=1, c=1, d=1 → p_o = 0.5, p_e = 0.5 → κ = 0 exactly.
        var r1 = new[] { true, true, false, false };
        var r2 = new[] { true, false, true, false };
        Assert.Equal(0.0, CohenKappa.Compute(r1, r2), 10);
    }

    [Fact]
    public void Known_two_by_two_yields_its_known_kappa()
    {
        // Confusion a=20 (TT), b=5 (TF), c=10 (FT), d=15 (FF):
        // p_o = 35/50 = 0.7; marginals r1=0.5, r2=0.6 → p_e = 0.5·0.6 + 0.5·0.4 = 0.5; κ = 0.2/0.5 = 0.4.
        var r1 = new List<bool>();
        var r2 = new List<bool>();
        Add(r1, r2, count: 20, a: true, b: true);
        Add(r1, r2, count: 5, a: true, b: false);
        Add(r1, r2, count: 10, a: false, b: true);
        Add(r1, r2, count: 15, a: false, b: false);

        Assert.Equal(0.4, CohenKappa.Compute(r1, r2), 10);
    }

    [Fact]
    public void Systematic_disagreement_is_negative()
    {
        var r1 = new[] { true, true, false, false };
        var r2 = new[] { false, false, true, true };
        Assert.True(CohenKappa.Compute(r1, r2) < 0);
    }

    [Fact]
    public void All_same_label_on_both_raters_is_handled_without_dividing_by_zero()
    {
        var allTrue = new[] { true, true, true, true };
        var allFalse = new[] { false, false, false, false };

        // Both constant + identical → trivial perfect agreement, returned as 1.0 (not NaN / not a throw).
        Assert.Equal(1.0, CohenKappa.Compute(allTrue, allTrue));
        Assert.Equal(1.0, CohenKappa.Compute(allFalse, allFalse));
        // Both constant but opposite → resolved to 0.0, still no divide-by-zero.
        Assert.Equal(0.0, CohenKappa.Compute(allTrue, allFalse));
    }

    [Fact]
    public void Mismatched_or_empty_arrays_throw()
    {
        bool[] one = [true];
        bool[] oneTwo = [true, false];
        bool[] empty = [];
        Assert.Throws<ArgumentException>(() => CohenKappa.Compute(one, oneTwo));
        Assert.Throws<ArgumentException>(() => CohenKappa.Compute(empty, empty));
    }

    private static void Add(List<bool> r1, List<bool> r2, int count, bool a, bool b)
    {
        for (var i = 0; i < count; i++)
        {
            r1.Add(a);
            r2.Add(b);
        }
    }
}
