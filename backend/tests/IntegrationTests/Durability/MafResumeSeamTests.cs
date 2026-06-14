using Backend.Core.Domain;
using Backend.Core.Orchestration;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Backend.IntegrationTests.Durability;

/// <summary>
/// The adversarial proof (DL-018): a real MAF graph runs behind the c1/c2 durable seam, and
/// the checkpoint→exit→resume round-trip survives a worker kill with no duplicated side
/// effects. Each segment job is invoked twice to simulate a crash + Hangfire re-run; the
/// status guards make the re-runs no-ops, so there is exactly one checkpoint, one asset, and
/// one publish.
/// </summary>
[Trait("Category", "Durability")]
public sealed class MafResumeSeamTests : IClassFixture<DurabilityFixture>
{
    private readonly DurabilityFixture _fixture;

    public MafResumeSeamTests(DurabilityFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task MafGraph_checkpoint_exit_resume_roundtrips_with_no_duplicate_sideeffects()
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA);

        // Segment 1: ExecuteRun runs the MAF generation graph → checkpoint → AwaitingApproval.
        var (execDb, execJob) = _fixture.CreateExecuteRunJob(_fixture.BrandA);
        await using (execDb) { await execJob.ExecuteAsync(runId, _fixture.BrandA); }

        // Worker killed, Hangfire retries segment 1: re-run → guarded no-op (already AwaitingApproval).
        var (exec2Db, exec2Job) = _fixture.CreateExecuteRunJob(_fixture.BrandA);
        await using (exec2Db) { await exec2Job.ExecuteAsync(runId, _fixture.BrandA); }

        var afterGen = await _fixture.ReadCheckpointStateAsync(runId, _fixture.BrandA);
        Assert.NotNull(afterGen);
        Assert.Equal(GraphPhase.AwaitingApproval, afterGen!.Phase);
        Assert.NotNull(afterGen.Caption);
        Assert.NotNull(afterGen.Media);
        Assert.NotNull(afterGen.Draft);
        Assert.Contains(afterGen.Trace.Spans, s => s.Node == "media" && s.Tool == "minio.put");

        await _fixture.ApproveRunAsync(runId, _fixture.BrandA);

        // Segment 2: a fresh ResumeRun rehydrates from the checkpoint → mock publish → Done.
        var (resDb, resJob) = _fixture.CreateResumeRunJob(_fixture.BrandA);
        await using (resDb) { await resJob.ExecuteAsync(runId, _fixture.BrandA); }

        // Worker killed, Hangfire retries segment 2: re-run → guarded no-op (already Done).
        var (res2Db, res2Job) = _fixture.CreateResumeRunJob(_fixture.BrandA);
        await using (res2Db) { await res2Job.ExecuteAsync(runId, _fixture.BrandA); }

        var final = await _fixture.ReadCheckpointStateAsync(runId, _fixture.BrandA);
        Assert.NotNull(final);
        Assert.Equal(GraphPhase.Done, final!.Phase);
        Assert.StartsWith("mock://meta/", final.Publish!.ExternalRef!);
        Assert.Equal("published", final.Publish.Status);
        Assert.Empty(final.Errors);

        // One continuous trace across the ExecuteRun → ResumeRun seam.
        Assert.False(string.IsNullOrEmpty(final.Trace.TraceId));
        Assert.Equal(final.Trace.Spans.Count, final.Trace.SpanIds.Count);
        Assert.Contains(final.Trace.Spans, s => s.Node == "strategy");
        Assert.Contains(final.Trace.Spans, s => s.Node == "publishing" && s.Tool == "meta.publish");

        var (readDb, scope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (readDb)
        {
            await using var handle = await scope.BeginAsync();

            var run = await readDb.AgentRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
            Assert.Equal(RunStatus.Done, run.Status);

            // Exactly one checkpoint across all four job invocations — no duplicate side effects.
            var checkpoints = await readDb.RunCheckpoints.AsNoTracking()
                .Where(c => c.AgentRunId == runId)
                .ToListAsync();
            Assert.Single(checkpoints);
        }
    }
}
