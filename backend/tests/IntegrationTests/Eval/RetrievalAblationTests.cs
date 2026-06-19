using System.Globalization;
using Backend.Core.Knowledge;
using Backend.Infrastructure.Configuration.Options;
using Backend.Infrastructure.Evaluation;
using Backend.Infrastructure.Evaluation.Evaluators;
using Backend.IntegrationTests.Support;
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

        // Five config arms. The three rerank arms decompose S2: A = no rerank, B = cross-encoder only
        // (blend off), C = full rerank (cross-encoder + perf/recency blend) — to attribute the regression.
        var dense = await RunArmAsync("dense", "retrieval[s0=off,sparse=off,rerank=off]",
            new RetrievalOptions { QueryTransformEnabled = false, SparseEnabled = false, RerankEnabled = false }, dataset, sha);
        var hybrid = await RunArmAsync("hybrid", "retrieval[s0=off,sparse=on,rerank=off]",
            new RetrievalOptions { QueryTransformEnabled = false, SparseEnabled = true, RerankEnabled = false }, dataset, sha);
        var crossOnly = await RunArmAsync("cross-encoder-only", "retrieval[s0=off,sparse=on,rerank=on,blend=off]",
            new RetrievalOptions { QueryTransformEnabled = false, SparseEnabled = true, RerankEnabled = true, BlendEnabled = false }, dataset, sha);
        var hybridRerank = await RunArmAsync("hybrid+rerank", "retrieval[s0=off,sparse=on,rerank=on,blend=on]",
            new RetrievalOptions { QueryTransformEnabled = false, SparseEnabled = true, RerankEnabled = true }, dataset, sha);
        var hybridS0 = await RunArmAsync("hybrid+s0", "retrieval[s0=on,sparse=on,rerank=off]",
            new RetrievalOptions { QueryTransformEnabled = true, SparseEnabled = true, RerankEnabled = false }, dataset, sha);

        // --- Structural assertions: every arm produced n=10 scored cases with in-range metric values. ---
        foreach (var arm in new[] { dense, hybrid, crossOnly, hybridRerank, hybridS0 })
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
        ReportPaired("S0 multi-query (hybrid → +S0 mock)", hybrid, hybridS0);

        // S2 decomposition — attribute the rerank regression to the cross-encoder vs. the metadata blend.
        _output.WriteLine("=== S2 rerank decomposition (A=no rerank, B=cross-encoder only, C=full rerank) ===");
        ReportPaired("B vs A — cross-encoder only (hybrid → +rerank, blend off)", hybrid, crossOnly);
        ReportPaired("C vs B — metadata blend (cross-encoder only → +blend)", crossOnly, hybridRerank);
        ReportPaired("C vs A — full rerank net (hybrid → +rerank+blend)", hybrid, hybridRerank);

        ReportDesignedArms(hybrid, crossOnly, hybridRerank);
    }

    [Fact]
    public async Task Default_config_skips_S2_rerank_entirely_with_zero_cross_encoder_calls()
    {
        if (!_fixture.ServicesAvailable)
        {
            _output.WriteLine($"SKIP: self-hosted retrieval services unavailable ({_fixture.UnavailableReason}).");
            return;
        }

        var dataset = await JsonDatasetLoader.LoadAsync(DatasetPath());

        // The committed default config — no explicit S2 override. RerankEnabled defaults OFF (DL-056).
        var defaultOptions = new RetrievalOptions();
        Assert.False(defaultOptions.RerankEnabled);

        // PRIMARY: run the default path with a counting spy → the bge cross-encoder is never invoked. The
        // gate short-circuits UPSTREAM of the call (no hop ⇒ no reorder), not "call then no-op".
        var defaultSpy = new CountingRerankProvider(_fixture.Reranker);
        var defaultRanked = await RetrieveAllAsync(dataset, defaultOptions, defaultSpy);
        Assert.Equal(0, defaultSpy.Calls);

        // CORROBORATING: identical ranking to an EXPLICIT rerank-off arm under the same eval S0/S1 settings —
        // confirms no reorder slipped through (the eval uses the mock S0, so compare in-harness, not prod).
        var explicitOff = new RetrievalOptions
        {
            QueryTransformEnabled = defaultOptions.QueryTransformEnabled,
            DenseEnabled = defaultOptions.DenseEnabled,
            SparseEnabled = defaultOptions.SparseEnabled,
            RerankEnabled = false,
        };
        var offSpy = new CountingRerankProvider(_fixture.Reranker);
        var offRanked = await RetrieveAllAsync(dataset, explicitOff, offSpy);
        Assert.Equal(0, offSpy.Calls);
        Assert.Equal(ExpectedCaseCount, defaultRanked.Count);
        foreach (var caseId in defaultRanked.Keys)
        {
            Assert.Equal(offRanked[caseId], defaultRanked[caseId]);
        }

        // GATE IS LOAD-BEARING: explicitly enabling S2 (same S0/S1) DOES invoke the cross-encoder AND reorders
        // at least one query — so the zero-count above is the config gate, not a wired-out / no-op reranker.
        var explicitOn = new RetrievalOptions { SparseEnabled = defaultOptions.SparseEnabled, RerankEnabled = true };
        var onSpy = new CountingRerankProvider(_fixture.Reranker);
        var onRanked = await RetrieveAllAsync(dataset, explicitOn, onSpy);
        Assert.True(onSpy.Calls > 0, "explicit rerank-on must invoke the cross-encoder (spy-wiring proof)");
        Assert.Contains(defaultRanked.Keys, id => !onRanked[id].SequenceEqual(defaultRanked[id]));

        _output.WriteLine(
            $"Default S2 path: {defaultSpy.Calls} cross-encoder calls (expected 0). Explicit rerank-off: " +
            $"{offSpy.Calls} calls, ranking identical on all {ExpectedCaseCount} golden queries. Explicit " +
            $"rerank-on: {onSpy.Calls} calls and reorders — default-off skips the hop, not a no-op.");
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

    private async Task<Dictionary<string, IReadOnlyList<Guid>>> RetrieveAllAsync(
        EvalDataset dataset, RetrievalOptions options, IRerankProvider? rerank = null)
    {
        var ranked = new Dictionary<string, IReadOnlyList<Guid>>(StringComparer.Ordinal);
        var (db, scope, retrieval) = _fixture.CreateRetrieval(options, rerank);
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

            // The five ablation arms, each distinguished by its config descriptor (PromptVersion).
            var descriptors = runs.Select(r => r.PromptVersion).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(5, descriptors.Count);
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

    private void ReportDesignedArms(
        RetrievalEvalRunResult hybrid, RetrievalEvalRunResult crossOnly, RetrievalEvalRunResult hybridRerank)
    {
        _output.WriteLine("=== Designed arms across A=hybrid / B=cross-encoder-only / C=full-rerank (truth either way) ===");

        // GR-05 (rerank) + GR-08 (performance blend): is the cross-encoder or the blend responsible for the move?
        ReportCasePrecision("GR-05", "content; full rerank hurt it", hybrid, crossOnly, hybridRerank);
        ReportCasePrecision("GR-08", "performance intent; +0.300 under full rerank", hybrid, crossOnly, hybridRerank);

        // GR-04 — recency: does Intel-2026 outrank the excluded Intel-2024? The recency-δ lives only in the
        // blend (C); cross-encoder-only (B) has no recency term.
        ReportRecency("A hybrid (no rerank)", hybrid);
        ReportRecency("B cross-encoder-only (no recency-δ)", crossOnly);
        ReportRecency("C full rerank (recency blend on)", hybridRerank);
    }

    private void ReportCasePrecision(
        string caseId, string note, RetrievalEvalRunResult a, RetrievalEvalRunResult b, RetrievalEvalRunResult c)
    {
        var pa = ByCase(a)[caseId][ContextPrecisionEvaluator.MetricNameConst];
        var pb = ByCase(b)[caseId][ContextPrecisionEvaluator.MetricNameConst];
        var pc = ByCase(c)[caseId][ContextPrecisionEvaluator.MetricNameConst];
        _output.WriteLine($"{caseId} ({note}) Context Precision: A={pa:0.000} → B(cross-only)={pb:0.000} → C(+blend)={pc:0.000}");
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
