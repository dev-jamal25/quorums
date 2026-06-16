using Backend.Core.Domain;
using Backend.IntegrationTests.Support;
using Xunit;

namespace Backend.IntegrationTests.Durability;

/// <summary>
/// Job-level proof that a fatal generation failure maps the <c>AgentRun</c> to
/// <see cref="RunStatus.Failed"/> (the checkpoint still carries the structured error for the
/// trace). Covers the global-ceiling breach and a fatal-node exhaustion across the durable seam.
/// </summary>
[Trait("Category", "Durability")]
public sealed class GenerationFailureSeamTests : IClassFixture<DurabilityFixture>
{
    private readonly DurabilityFixture _fixture;

    public GenerationFailureSeamTests(DurabilityFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Global_ceiling_breach_marks_the_run_failed()
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA);
        // $0.05 < the $0.057 accumulated pre-fork spend (Strategist + selection + CD) → the Media
        // gate breaches on pre-fork spend alone, and the job maps the AgentRun to Failed.
        var (db, job) = _fixture.CreateExecuteRunJob(
            _fixture.BrandA, TestGeneration.Deps(globalCeilingUsd: 0.05m));
        await using (db)
        {
            await job.ExecuteAsync(runId, _fixture.BrandA);
        }

        Assert.Equal(RunStatus.Failed, await _fixture.ReadRunStatusAsync(runId, _fixture.BrandA));
        var state = await _fixture.ReadCheckpointStateAsync(runId, _fixture.BrandA);
        Assert.NotNull(state!.FatalError);
        Assert.Equal("budget.ceiling_exceeded", state.FatalError!.Code);
    }

    [Fact]
    public async Task Strategist_exhaustion_marks_the_run_failed()
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA);
        var (db, job) = _fixture.CreateExecuteRunJob(
            _fixture.BrandA, TestGeneration.Deps(failTools: ["record_strategy_candidates"]));
        await using (db)
        {
            await job.ExecuteAsync(runId, _fixture.BrandA);
        }

        Assert.Equal(RunStatus.Failed, await _fixture.ReadRunStatusAsync(runId, _fixture.BrandA));
    }
}
