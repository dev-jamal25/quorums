using System.Globalization;
using Backend.Infrastructure.Configuration.Options;
using Backend.Infrastructure.Evaluation;
using Backend.Infrastructure.Evaluation.Evaluators;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Backend.IntegrationTests.Eval;

/// <summary>
/// The reference-based retrieval eval + paired stage ablation (DL-048/025). Drives the REAL hybrid pipeline
/// (S0 deterministic multi-query mock → S1 pgvector dense ∪ Postgres FTS sparse → S2 bge cross-encoder
/// rerank) over the 10 golden queries under the demo brand's RLS scope, scores each with the rank metrics,
/// dual-writes each config arm to the disk + Postgres eval store, and compares stages paired per-query with
/// the DL-048 statistical discipline (n reported, &lt;5-point deltas treated as noise, paired win-counts).
/// No LLM, no API keys — the self-hosted services carry the dense/rerank signal. If they are down the test
/// skips EXPLICITLY (never passes on empty results).
/// </summary>
[Trait("Category", "Eval")]
public sealed class RetrievalAblationTests : IClassFixture<RetrievalAblationFixture>
{
    private const double NoiseBand = 0.05;          // DL-048: deltas under ~5 points are noise at this n.
    private const int ExpectedCaseCount = 10;
    private const int MetricsPerCase = 5;           // recall@1, recall@3, Hit@1, MRR, Context Precision

    // The two near-identical market-intel chunks for the GR-04 recency arm (resolved for the demo brand).
    private static readonly Guid _intel2026 = Guid.Parse("87ee333d-316f-bf09-b65d-6baaffef6c3a");
    private static readonly Guid _intel2024 = Guid.Parse("91ed5b46-e1db-47b2-4566-987a1c5cbdb0");

    private readonly RetrievalAblationFixture _fixture;
    private readonly ITestOutputHelper _output;

    public RetrievalAblationTests(RetrievalAblationFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task Ablation_runs_real_pipeline_persists_each_arm_and_reports_paired_deltas()
    {
        if (!_fixture.ServicesAvailable)
        {
            // Explicit skip — never assert green on empty results (xUnit v2 has no dynamic Assert.Skip).
            _output.WriteLine($"SKIP: self-hosted retrieval services unavailable ({_fixture.UnavailableReason}). " +
                "Bring up tei-embed + tei-rerank (docker compose) to run the real ablation.");
            return;
        }

        var dataset = await JsonDatasetLoader.LoadAsync(DatasetPath());
        Assert.Equal(ExpectedCaseCount, dataset.Cases.Count);
        var sha = GitInfo.HeadSha();

        // Four config arms; each pairwise comparison isolates one DL-025 stage toggle.
        var dense = await RunArmAsync("dense", "retrieval[s0=off,sparse=off,rerank=off]",
            new RetrievalOptions { QueryTransformEnabled = false, SparseEnabled = false, RerankEnabled = false }, dataset, sha);
        var hybrid = await RunArmAsync("hybrid", "retrieval[s0=off,sparse=on,rerank=off]",
            new RetrievalOptions { QueryTransformEnabled = false, SparseEnabled = true, RerankEnabled = false }, dataset, sha);
        var hybridRerank = await RunArmAsync("hybrid+rerank", "retrieval[s0=off,sparse=on,rerank=on]",
            new RetrievalOptions { QueryTransformEnabled = false, SparseEnabled = true, RerankEnabled = true }, dataset, sha);
        var hybridS0 = await RunArmAsync("hybrid+s0", "retrieval[s0=on,sparse=on,rerank=off]",
            new RetrievalOptions { QueryTransformEnabled = true, SparseEnabled = true, RerankEnabled = false }, dataset, sha);

        // --- Structural assertions: every arm produced n=10 scored cases with in-range metric values. ---
        foreach (var arm in new[] { dense, hybrid, hybridRerank, hybridS0 })
        {
            Assert.Equal(ExpectedCaseCount, arm.Cases.Count);
            Assert.All(arm.Cases, c =>
            {
                Assert.Equal(MetricsPerCase, c.Metrics.Count);
                Assert.All(c.Metrics.Values, v => Assert.InRange(v, 0.0, 1.0));
            });
        }

        // Sanity floor: the full pipeline produces real signal (not empty / broken) — NOT a fabricated delta.
        Assert.True(Mean(hybridRerank, ReciprocalRankEvaluator.MetricNameConst) > 0,
            "the full hybrid+rerank arm retrieved no relevant chunk for any query");
        Assert.True(Mean(hybridRerank, ContextRecallEvaluator.Name(3)) > 0,
            "the full hybrid+rerank arm has zero recall@3");

        // --- Persistence: each arm is its own RLS-scoped EvalRun (config rides PromptVersion) with 50 rows. ---
        await AssertArmsPersistedAsync(sha);

        // --- Paired stage ablation (DL-048): per-query deltas + aggregate + paired win-count, honestly. ---
        _output.WriteLine($"=== Paired stage ablation — n={ExpectedCaseCount}, noise band = {NoiseBand:0.00} (deltas within are noise) ===");
        ReportPaired("S1 sparse (dense-only → +sparse)", dense, hybrid);
        ReportPaired("S2 rerank (hybrid → +rerank)", hybrid, hybridRerank);
        ReportPaired("S0 multi-query (hybrid → +S0 mock)", hybrid, hybridS0);

        ReportDesignedArms(hybrid, hybridRerank);
    }

    // ---- arm execution ----------------------------------------------------------------------------------

    private async Task<RetrievalEvalRunResult> RunArmAsync(
        string label, string descriptor, RetrievalOptions options, EvalDataset dataset, string sha)
    {
        var ranked = await RetrieveAllAsync(dataset, options);

        var reporting = EvalReportingFactory.CreateDiskReporting(
            RetrievalRankEvaluators.All(), _fixture.StorageRoot, executionName: "ablation-" + label);

        var (persistence, db) = _fixture.CreatePersistence();
        await using (db)
        {
            var runner = new RetrievalEvalRunner(persistence);
            return await runner.RunAsync(
                RetrievalAblationFixture.DemoBrand,
                dataset,
                reporting,
                ranked,
                new EvalRunMetadata(sha, descriptor, "pgvector+nomic-embed+bge-rerank", "nomic-embed-text-v1.5/bge-reranker-v2-m3", 0d));
        }
    }

    private async Task<Dictionary<string, IReadOnlyList<Guid>>> RetrieveAllAsync(EvalDataset dataset, RetrievalOptions options)
    {
        var ranked = new Dictionary<string, IReadOnlyList<Guid>>(StringComparer.Ordinal);
        var (db, scope, retrieval) = _fixture.CreateRetrieval(options);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();
            foreach (var evalCase in dataset.Cases)
            {
                var query = evalCase.Input.GetProperty("query").GetString()!;
                var result = await retrieval.Retrieve(query, RetrievalAblationFixture.DemoBrand, docType: null, k: options.FinalK);
                ranked[evalCase.Id] = result.Chunks.Select(c => c.ChunkId).ToList();
            }
        }

        return ranked;
    }

    // ---- persistence ------------------------------------------------------------------------------------

    private async Task AssertArmsPersistedAsync(string sha)
    {
        var (db, scope) = _fixture.CreateBrandScopedContext();
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            var runs = await db.EvalRuns.AsNoTracking()
                .Where(r => r.DatasetName == "golden-retrieval" && r.GitSha == sha)
                .ToListAsync();

            // The four ablation arms, each distinguished by its config descriptor (PromptVersion).
            var descriptors = runs.Select(r => r.PromptVersion).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(4, descriptors.Count);
            Assert.All(runs, r => Assert.Equal(RetrievalAblationFixture.DemoBrand, r.BrandId));

            foreach (var run in runs)
            {
                var rows = await db.EvalResults.AsNoTracking().Where(r => r.RunId == run.Id).ToListAsync();
                Assert.Equal(ExpectedCaseCount * MetricsPerCase, rows.Count);
                Assert.All(rows, r => Assert.Equal(RetrievalAblationFixture.DemoBrand, r.BrandId));
                Assert.Contains(ContextPrecisionEvaluator.MetricNameConst, run.Aggregates.Keys);
            }
        }
    }

    // ---- paired reporting -------------------------------------------------------------------------------

    private void ReportPaired(string toggle, RetrievalEvalRunResult off, RetrievalEvalRunResult on)
    {
        _output.WriteLine($"--- {toggle} ---");
        ReportMetric(ContextPrecisionEvaluator.MetricNameConst, off, on);
        ReportMetric(ReciprocalRankEvaluator.MetricNameConst, off, on);
    }

    private void ReportMetric(string metric, RetrievalEvalRunResult off, RetrievalEvalRunResult on)
    {
        var offByCase = ByCase(off);
        var onByCase = ByCase(on);

        var deltas = new List<double>();
        int onWins = 0, offWins = 0, ties = 0;
        var perCase = new List<string>();

        foreach (var caseId in offByCase.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var o = offByCase[caseId][metric];
            var n = onByCase[caseId][metric];
            var d = n - o;
            deltas.Add(d);

            if (d > NoiseBand)
            {
                onWins++;
            }
            else if (d < -NoiseBand)
            {
                offWins++;
            }
            else
            {
                ties++;
            }

            perCase.Add($"{caseId} {o:0.000}→{n:0.000} (Δ{d:+0.000;-0.000; 0.000})");
        }

        var mean = deltas.Average();
        var verdict = Math.Abs(mean) < NoiseBand ? "within noise" : (mean > 0 ? "improves" : "regresses");
        _output.WriteLine($"  {metric}: mean Δ={mean:+0.000;-0.000; 0.000} ({verdict}); " +
            $"paired wins on>off={onWins}, off>on={offWins}, ties={ties} (n={deltas.Count})");
        _output.WriteLine("    " + string.Join("  |  ", perCase));
    }

    private void ReportDesignedArms(RetrievalEvalRunResult hybrid, RetrievalEvalRunResult hybridRerank)
    {
        _output.WriteLine("=== Designed arms (report the truth either way) ===");

        // GR-05 — rerank should lift the rank-aware precision (spans Mission + Yirgacheffe above noise docs).
        var gr05Off = ByCase(hybrid).GetValueOrDefault("GR-05")?[ContextPrecisionEvaluator.MetricNameConst];
        var gr05On = ByCase(hybridRerank).GetValueOrDefault("GR-05")?[ContextPrecisionEvaluator.MetricNameConst];
        _output.WriteLine($"GR-05 (rerank) Context Precision: hybrid={gr05Off:0.000} → +rerank={gr05On:0.000} " +
            $"(Δ{(gr05On - gr05Off):+0.000;-0.000; 0.000})");

        // GR-04 — recency: in the rerank arm (where the recency blend is active), does Intel-2026 outrank
        // the excluded Intel-2024?
        ReportRecency("hybrid (rerank off)", hybrid);
        ReportRecency("hybrid+rerank (recency blend on)", hybridRerank);
    }

    private void ReportRecency(string label, RetrievalEvalRunResult arm)
    {
        var ranked = arm.Cases.First(c => c.CaseId == "GR-04").RankedChunkIds;
        var p2026 = ranked.ToList().IndexOf(_intel2026);
        var p2024 = ranked.ToList().IndexOf(_intel2024);
        var outranks = p2026 >= 0 && (p2024 < 0 || p2026 < p2024);
        _output.WriteLine($"GR-04 (recency) [{label}]: Intel-2026 rank={Rank(p2026)}, Intel-2024 rank={Rank(p2024)} " +
            $"→ fresher-outranks-stale = {outranks}");
    }

    private static string Rank(int index) => index < 0 ? "absent" : (index + 1).ToString(CultureInfo.InvariantCulture);

    private static double Mean(RetrievalEvalRunResult arm, string metric) =>
        arm.Cases.Select(c => c.Metrics[metric]).Average();

    private static Dictionary<string, IReadOnlyDictionary<string, double>> ByCase(RetrievalEvalRunResult arm) =>
        arm.Cases.ToDictionary(c => c.CaseId, c => c.Metrics, StringComparer.Ordinal);

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

        return Path.Combine(dir.FullName, "eval", "datasets", "552732e7-0d74-4e58-9fdd-b6454479a38a", "golden-retrieval.json");
    }
}
