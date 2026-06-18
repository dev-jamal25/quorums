using Backend.Core.Evaluation;
using Backend.Infrastructure.Evaluation;
using Backend.Infrastructure.Evaluation.Evaluators;
using Backend.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI.Evaluation;
using Xunit;

namespace Backend.IntegrationTests.Eval;

/// <summary>
/// End-to-end harness path on Microsoft.Extensions.AI.Evaluation: a mock-mode generation is projected to
/// <see cref="SystemOutput"/>, the rule-based evaluators run via the library <c>ScenarioRun</c>, and the
/// results are dual-written to the disk store (`dotnet aieval`) AND the RLS-scoped Postgres run store.
/// Proves a failed run still persists with its git SHA + dataset version. Deterministic → zero API spend.
/// </summary>
[Trait("Category", "Eval")]
public sealed class EvalHarnessIntegrationTests : IClassFixture<EvalFixture>
{
    private const int CaseCount = 3;     // fixture dataset
    private const int EvaluatorCount = 7; // rule-based §1 evaluators

    private readonly EvalFixture _fixture;

    public EvalHarnessIntegrationTests(EvalFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Mock_run_projects_evaluates_and_dual_writes_to_disk_and_postgres()
    {
        var output = await RunMockGenerationAsync();
        var dataset = await JsonDatasetLoader.LoadAsync(DatasetPath());
        var sha = GitInfo.HeadSha();

        var run = await RunHarnessAsync(dataset, output, sha, "eval-it-happy");

        // Disk store written (this is what `dotnet aieval report` reads).
        Assert.True(Directory.Exists(_fixture.StorageRoot));
        Assert.NotEmpty(Directory.GetFiles(_fixture.StorageRoot, "*", SearchOption.AllDirectories));

        // Postgres run store, queryable under the brand scope.
        var (db, scope) = _fixture.CreateBrandScopedContext();
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            var persisted = await db.EvalRuns.AsNoTracking().FirstAsync(r => r.Id == run.Id);
            Assert.Equal(_fixture.BrandId, persisted.BrandId);
            Assert.Equal(sha, persisted.GitSha);
            Assert.Equal("1.0.0", persisted.DatasetVersion);
            Assert.Equal("tool-call-fixture", persisted.DatasetName);

            var results = await db.EvalResults.AsNoTracking().Where(r => r.RunId == run.Id).ToListAsync();
            Assert.Equal(CaseCount * EvaluatorCount, results.Count);
            Assert.All(results, r => Assert.Equal(_fixture.BrandId, r.BrandId));
        }
    }

    [Fact]
    public async Task Forced_violation_reds_the_metric_yet_the_failed_run_still_persists()
    {
        // Forced schema violation: the Content Strategist tool fails on every attempt.
        var (deps, retrieval, chat) = TestGeneration.EvalDeps(failTools: ["record_strategy_candidates"]);
        var orchestrator = TestGeneration.Orchestrator(deps);
        var state = await orchestrator.RunGenerationAsync(TestGeneration.Seed(Guid.NewGuid(), _fixture.BrandId));
        var (injected, retries) = TestGeneration.OffState(retrieval, chat);
        var output = SystemOutputProjector.Project(state, injected, retries);

        // The schema-validity metric is RED on this bad output, and the strategist retried exactly twice.
        var (messages, response) = EvalTestData.Conversation();
        var context = new SystemOutputContext(output, EvalTestData.Case());
        var schemaResult = await new SchemaValidityEvaluator().EvaluateAsync(messages, response, additionalContext: [context]);
        Assert.Equal(false, schemaResult.Get<BooleanMetric>(SchemaValidityEvaluator.MetricNameConst).Value);
        Assert.Equal(2, retries[SystemOutput.Nodes.ContentStrategist]);

        // Yet the harness still persists the failed run + its 0.0 metric with SHA + dataset version.
        var dataset = await JsonDatasetLoader.LoadAsync(DatasetPath());
        var sha = GitInfo.HeadSha();
        var run = await RunHarnessAsync(dataset, output, sha, "eval-it-adversarial");

        var (db, scope) = _fixture.CreateBrandScopedContext();
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            var persisted = await db.EvalRuns.AsNoTracking().FirstAsync(r => r.Id == run.Id);
            Assert.Equal(sha, persisted.GitSha);
            Assert.Equal("1.0.0", persisted.DatasetVersion);

            var schemaRows = await db.EvalResults.AsNoTracking()
                .Where(r => r.RunId == run.Id && r.EvaluatorName == SchemaValidityEvaluator.MetricNameConst)
                .ToListAsync();
            Assert.NotEmpty(schemaRows);
            Assert.All(schemaRows, r => Assert.Equal(0d, r.Score));
        }
    }

    private async Task<SystemOutput> RunMockGenerationAsync()
    {
        var (deps, retrieval, chat) = TestGeneration.EvalDeps();
        var orchestrator = TestGeneration.Orchestrator(deps);
        var state = await orchestrator.RunGenerationAsync(TestGeneration.Seed(Guid.NewGuid(), _fixture.BrandId));
        var (injected, retries) = TestGeneration.OffState(retrieval, chat);
        return SystemOutputProjector.Project(state, injected, retries);
    }

    private async Task<Backend.Core.Domain.EvalRun> RunHarnessAsync(
        EvalDataset dataset, SystemOutput output, string gitSha, string executionName)
    {
        var reporting = EvalReportingFactory.CreateDiskReporting(
            RuleBasedEvaluators.All(TestGeneration.Constraints()),
            _fixture.StorageRoot,
            executionName: executionName);

        var (persistence, db) = _fixture.CreatePersistence();
        await using (db)
        {
            var runner = new EvalScenarioRunner(persistence);
            return await runner.RunAsync(
                _fixture.BrandId,
                dataset,
                reporting,
                EvalScenarioRunner.SingleOutput(dataset, output),
                new EvalRunMetadata(gitSha, "unversioned", "test-sonnet", "1", 0d));
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

        return Path.Combine(dir.FullName, "eval", "datasets", "552732e7-0d74-4e58-9fdd-b6454479a38a", "tool-call-fixture.json");
    }
}
