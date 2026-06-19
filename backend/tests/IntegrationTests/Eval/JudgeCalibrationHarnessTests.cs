using Backend.Infrastructure.Evaluation;
using Backend.Infrastructure.Evaluation.Judges;
using Backend.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI.Evaluation;
using Xunit;

namespace Backend.IntegrationTests.Eval;

/// <summary>
/// The calibration harness (binarize → Cohen's κ → RLS-scoped persist) proven with a deterministic fake
/// judge — zero spend, no Gemini. A "perfect" judge that echoes the human labels yields κ = 1.0 on both
/// axes and persists; a lazy "always-pass" judge collapses κ below the 0.6 gate. Confirms the wiring is
/// honest before any real calibration spend. The live calibration (real Gemini) is the separate, spending
/// test.
/// </summary>
[Trait("Category", "Eval")]
public sealed class JudgeCalibrationHarnessTests : IClassFixture<EvalFixture>
{
    private readonly EvalFixture _fixture;

    public JudgeCalibrationHarnessTests(EvalFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_judge_that_matches_the_human_labels_scores_kappa_one_and_persists_rls_scoped()
    {
        var set = await JudgeCalibrationDataset.LoadAsync(DatasetPath());
        Assert.Equal(16, set.N);

        var result = await RunAsync(set, PerfectJudge(set));

        Assert.Equal(1.0, result.KappaBrand);
        Assert.Equal(1.0, result.KappaGrounded);

        var (db, scope) = _fixture.CreateBrandScopedContext();
        await using (db)
        {
            await using var handle = await scope.BeginAsync();
            var persisted = await db.EvalRuns.AsNoTracking().FirstAsync(r => r.Id == result.Run.Id);
            Assert.Equal("judge-calibration", persisted.DatasetName);
            Assert.Contains("kappa.brand", persisted.Aggregates.Keys);
            Assert.Contains("kappa.grounded", persisted.Aggregates.Keys);

            var rows = await db.EvalResults.AsNoTracking().Where(r => r.RunId == result.Run.Id).ToListAsync();
            Assert.Equal(set.N * 2, rows.Count); // brand + grounded per item
            Assert.All(rows, r => Assert.Equal(_fixture.BrandId, r.BrandId));
        }
    }

    [Fact]
    public async Task A_lazy_always_pass_judge_collapses_kappa_below_the_gate()
    {
        var set = await JudgeCalibrationDataset.LoadAsync(DatasetPath());

        // Always "on" / "grounded" regardless of the item → no better than chance vs the mixed human labels.
        var result = await RunAsync(set, _ => "{\"voice_tone\":5,\"audience_fit\":5,\"visual_style\":5,\"injection_resistance\":5,\"groundedness\":5,\"reasoning\":\"x\"}");

        Assert.True(result.KappaBrand < 0.6, $"a constant judge must miss the κ≥0.6 gate (got {result.KappaBrand})");
        Assert.True(result.KappaGrounded < 0.6, $"a constant judge must miss the κ≥0.6 gate (got {result.KappaGrounded})");
    }

    private async Task<JudgeCalibrationResult> RunAsync(JudgeCalibrationSet set, Func<string, string> respond)
    {
        // A unique cache root per run so the two fakes never read each other's cached verdicts.
        var root = Path.Combine(Path.GetTempPath(), "judge-harness-" + Guid.NewGuid().ToString("N"));
        var reporting = EvalReportingFactory.CreateJudgeReporting(
            [new BrandConsistencyEvaluator(4), new GroundednessJudgeEvaluator(4)],
            new ChatConfiguration(new CannedJudgeChatClient(respond)),
            root,
            executionName: "judge-harness-it");

        var (persistence, db) = _fixture.CreatePersistence();
        try
        {
            await using (db)
            {
                var runner = new JudgeCalibrationRunner(persistence);
                return await runner.RunAsync(
                    _fixture.BrandId, set, reporting,
                    new EvalRunMetadata(GitInfo.HeadSha(), "judge-v1", "gemini-2.5-flash", "1", 0d));
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static Func<string, string> PerfectJudge(JudgeCalibrationSet set) => prompt =>
    {
        var match = set.Cases
            .Where(c => prompt.Contains(c.Output, StringComparison.Ordinal))
            .OrderByDescending(c => c.Output.Length)
            .First();

        if (prompt.Contains("voice_tone", StringComparison.Ordinal))
        {
            var s = match.BrandOn ? 5 : 1;
            return $"{{\"voice_tone\":{s},\"audience_fit\":{s},\"visual_style\":{s},\"injection_resistance\":{s},\"reasoning\":\"x\"}}";
        }

        var g = match.Grounded ? 5 : 1;
        return $"{{\"groundedness\":{g},\"reasoning\":\"x\"}}";
    };

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort temp cleanup
        }
    }

    private static string DatasetPath()
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

        return Path.Combine(dir.FullName, "eval", "datasets", "552732e7-0d74-4e58-9fdd-b6454479a38a", "judge-calibration.json");
    }
}
