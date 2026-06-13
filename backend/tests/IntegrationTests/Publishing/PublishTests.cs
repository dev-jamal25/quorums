using Backend.Core.Common;
using Backend.Core.Domain;
using Backend.IntegrationTests.Durability;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Backend.IntegrationTests.Publishing;

/// <summary>
/// Exercises the publish seam end-to-end through the durable jobs: ExecuteRun →
/// approve → ResumeRun publishes via <c>MockMetaIntegration</c>. Reuses the
/// durability fixture (real Postgres + RLS-bound contexts).
/// </summary>
[Trait("Category", "Publish")]
public sealed class PublishTests : IClassFixture<DurabilityFixture>
{
    private readonly DurabilityFixture _fixture;

    public PublishTests(DurabilityFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ResumeRun_publishes_via_mock_and_records_external_ref_and_done()
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA);

        var (execDb, execJob) = _fixture.CreateExecuteRunJob(_fixture.BrandA);
        await using (execDb) { await execJob.ExecuteAsync(runId, _fixture.BrandA); }

        await _fixture.ApproveRunAsync(runId, _fixture.BrandA);

        var (resumeDb, resumeJob) = _fixture.CreateResumeRunJob(_fixture.BrandA);
        await using (resumeDb) { await resumeJob.ExecuteAsync(runId, _fixture.BrandA); }

        var state = await _fixture.ReadCheckpointStateAsync(runId, _fixture.BrandA);
        Assert.NotNull(state);
        Assert.NotNull(state!.Publish);
        Assert.False(string.IsNullOrWhiteSpace(state.Publish!.ExternalRef));
        Assert.StartsWith("mock://meta/", state.Publish.ExternalRef!);
        Assert.Equal("published", state.Publish.Status);
        Assert.Empty(state.Errors);

        var (readDb, scope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (readDb)
        {
            await using var handle = await scope.BeginAsync();
            var run = await readDb.AgentRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
            Assert.Equal(RunStatus.Done, run.Status);
        }
    }

    [Fact]
    public async Task Mock_publish_is_deterministic_for_the_same_content_id()
    {
        // The publish key is the run id; a retried publish must re-use the same ref.
        var runId = Guid.NewGuid();
        var expected = $"mock://meta/{DeterministicGuid.From(runId, "meta")}";

        var meta = new Backend.Infrastructure.Integrations.Meta.MockMetaIntegration();
        var first = await meta.PublishAsync(new Core.Integrations.PublishRequest(
            BrandId: Guid.NewGuid(), ContentItemId: runId, Caption: "c", MediaStorageKey: "k"));
        var second = await meta.PublishAsync(new Core.Integrations.PublishRequest(
            BrandId: Guid.NewGuid(), ContentItemId: runId, Caption: "c", MediaStorageKey: "k"));

        Assert.Equal(expected, first.ExternalRef);
        Assert.Equal(first.ExternalRef, second.ExternalRef);
    }
}
