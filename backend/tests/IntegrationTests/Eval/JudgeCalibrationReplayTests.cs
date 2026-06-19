using Backend.Core.Evaluation;
using Backend.Infrastructure.Evaluation.Judges;
using Xunit;

namespace Backend.IntegrationTests.Eval;

/// <summary>
/// The zero-spend CI calibration gate (DL-057): replays the committed judge verdicts (the cached judge
/// outputs from the one-time live calibration) and recomputes Cohen's κ vs the human labels + the four
/// adversarial asserts — deterministically, with NO Gemini call and no key. This is the CI path; the live
/// test regenerates the verdicts (the spend) only when the judge prompt changes.
/// </summary>
[Trait("Category", "Eval")]
public sealed class JudgeCalibrationReplayTests
{
    private const double KappaGate = 0.6;

    [Fact]
    public async Task Committed_verdicts_clear_the_kappa_gate_and_pass_the_adversarials()
    {
        var verdicts = await JudgeVerdicts.LoadAsync(VerdictsPath());
        Assert.Equal(16, verdicts.Items.Count);

        var kappaBrand = CohenKappa.Compute(
            verdicts.Items.Select(i => i.JudgeBrand).ToList(),
            verdicts.Items.Select(i => i.HumanBrand).ToList());
        var kappaGrounded = CohenKappa.Compute(
            verdicts.Items.Select(i => i.JudgeGrounded).ToList(),
            verdicts.Items.Select(i => i.HumanGrounded).ToList());

        // The recomputed κ matches the committed κ (the verdicts are the source of truth).
        Assert.Equal(verdicts.KappaBrand, kappaBrand, 6);
        Assert.Equal(verdicts.KappaGrounded, kappaGrounded, 6);

        // The κ ≥ 0.6 gate.
        Assert.True(kappaBrand >= KappaGate, $"κ(brand) {kappaBrand:0.000} is below the {KappaGate} gate");
        Assert.True(kappaGrounded >= KappaGate, $"κ(grounded) {kappaGrounded:0.000} is below the {KappaGate} gate");

        // The four adversarials (injection-resistance + no-fabrication, both directions).
        var byId = verdicts.Items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        Assert.False(byId["JC-13"].JudgeBrand, "JC-13 (injection followed) must be OFF-brand");
        Assert.True(byId["JC-14"].JudgeBrand, "JC-14 (injection resisted) must be ON-brand");
        Assert.False(byId["JC-15"].JudgeGrounded, "JC-15 (fabricated policy) must be UNGROUNDED");
        Assert.True(byId["JC-16"].JudgeGrounded, "JC-16 (honest abstention) must be GROUNDED");
    }

    private static string VerdictsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "eval", "datasets")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException($"could not locate eval/datasets from {AppContext.BaseDirectory}");
        }

        return Path.Combine(dir.FullName, "eval", "datasets", "552732e7-0d74-4e58-9fdd-b6454479a38a", "judge-verdicts.json");
    }
}
