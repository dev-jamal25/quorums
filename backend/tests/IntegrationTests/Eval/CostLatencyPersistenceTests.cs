using Backend.Core.Evaluation;
using Backend.Core.Generation.Cost;
using Backend.Core.Orchestration;
using Backend.Infrastructure.Evaluation;
using Backend.Infrastructure.Evaluation.Evaluators;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Backend.IntegrationTests.Eval;

/// <summary>
/// The §3 cost &amp; latency metrics persist RLS-scoped per eval run, exactly like the rank/rule-based
/// metrics — driven through the production <see cref="EvalScenarioRunner"/> dual-write path over a
/// <b>synthetic</b> durable-record output (known tokens / media / span timings). No real generation, no
/// LLM: deterministic and zero API spend.
/// </summary>
[Trait("Category", "Eval")]
public sealed class CostLatencyPersistenceTests : IClassFixture<EvalFixture>
{
    private static readonly CostPrices _prices = new(3.0m, 15.0m, 1.0m, 5.0m, GeminiPerImage: 0.04m);

    private readonly EvalFixture _fixture;

    public CostLatencyPersistenceTests(EvalFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Cost_and_latency_persist_rls_scoped_with_their_values()
    {
        var dataset = await JsonDatasetLoader.LoadAsync(DatasetPath());
        var sha = GitInfo.HeadSha();
        var output = SyntheticOutput();

        var reporting = EvalReportingFactory.CreateDiskReporting(
            CostLatencyEvaluators.All(_prices), _fixture.StorageRoot, executionName: "cost-latency-it");

        var (persistence, db) = _fixture.CreatePersistence();
        Backend.Core.Domain.EvalRun run;
        await using (db)
        {
            var runner = new EvalScenarioRunner(persistence);
            run = await runner.RunAsync(
                _fixture.BrandId,
                dataset,
                reporting,
                EvalScenarioRunner.SingleOutput(dataset, output),
                new EvalRunMetadata(sha, "unversioned", "test-sonnet", "1", 0d));
        }

        var expectedCost = (double)CostModel.RunCostUsd(output.Budget.TokensSpent, output.GeminiCallCount, _prices);

        var (rdb, scope) = _fixture.CreateBrandScopedContext();
        await using (rdb)
        {
            await using var handle = await scope.BeginAsync();

            var rows = await rdb.EvalResults.AsNoTracking().Where(r => r.RunId == run.Id).ToListAsync();
            Assert.NotEmpty(rows);
            Assert.All(rows, r => Assert.Equal(_fixture.BrandId, r.BrandId));

            var costRows = rows.Where(r => r.EvaluatorName == CostEvaluator.MetricNameConst).ToList();
            var latencyRows = rows.Where(r => r.EvaluatorName == LatencyEvaluator.MetricNameConst).ToList();
            Assert.Equal(dataset.Cases.Count, costRows.Count);
            Assert.Equal(dataset.Cases.Count, latencyRows.Count);
            Assert.All(costRows, r => Assert.Equal(expectedCost, r.Score, 6));
            Assert.All(latencyRows, r => Assert.Equal(400.0, r.Score));

            Assert.Contains(CostEvaluator.MetricNameConst, run.Aggregates.Keys);
            Assert.Contains(LatencyEvaluator.MetricNameConst, run.Aggregates.Keys);
        }
    }

    private static SystemOutput SyntheticOutput()
    {
        var t0 = new DateTimeOffset(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);
        return EvalTestData.ValidOutput() with
        {
            Budget = new Budget(TokenBudget: 10_000_000, TokensSpent: 1_000_000, MediaBudget: 1m, MediaSpent: 0.08m),
            GeminiCallCount = 2,
            Trace = new TraceRefs("trace-1", ["s1", "s2"],
            [
                new TraceSpan("s1", "strategy", null, "ok", t0, t0.AddMilliseconds(100), null),
                new TraceSpan("s2", "copywriting", null, "ok", t0.AddMilliseconds(150), t0.AddMilliseconds(400), null),
            ]),
        };
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

        return Path.Combine(dir.FullName, "eval", "datasets", "552732e7-0d74-4e58-9fdd-b6454479a38a", "tool-call-fixture.json");
    }
}
