using Backend.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Backend.IntegrationTests.Durability;

[Trait("Category", "Durability")]
public sealed class DurabilityTests : IClassFixture<DurabilityFixture>
{
    private readonly DurabilityFixture _fixture;

    public DurabilityTests(DurabilityFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ExecuteRun_checkpoints_and_ends_with_awaiting_approval()
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA);

        var (execDb, execJob) = _fixture.CreateExecuteRunJob(_fixture.BrandA);
        await using (execDb)
        {
            await execJob.ExecuteAsync(runId, _fixture.BrandA);
        }

        var (readDb, scope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (readDb)
        {
            await using var handle = await scope.BeginAsync();

            var run = await readDb.AgentRuns.AsNoTracking()
                .FirstAsync(r => r.Id == runId);
            Assert.Equal(RunStatus.AwaitingApproval, run.Status);

            var checkpoint = await readDb.RunCheckpoints.AsNoTracking()
                .FirstOrDefaultAsync(c => c.AgentRunId == runId);
            Assert.NotNull(checkpoint);
            Assert.False(string.IsNullOrWhiteSpace(checkpoint.StateJson));
        }
    }

    [Fact]
    public async Task ResumeRun_in_fresh_scope_reconstructs_from_checkpoint_and_reaches_done()
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA);

        // Phase 1: ExecuteRun — scope 1
        var (execDb, execJob) = _fixture.CreateExecuteRunJob(_fixture.BrandA);
        await using (execDb) { await execJob.ExecuteAsync(runId, _fixture.BrandA); }

        // Simulate approval: mirrors the controller's AwaitingApproval → Publishing transition
        await _fixture.ApproveRunAsync(runId, _fixture.BrandA);

        // Phase 2: ResumeRun — scope 2 (fresh DbContext, fresh BrandContext, fresh BrandScope)
        var (resumeDb, resumeJob) = _fixture.CreateResumeRunJob(_fixture.BrandA);
        await using (resumeDb) { await resumeJob.ExecuteAsync(runId, _fixture.BrandA); }

        // Verification — scope 3
        var (readDb, scope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (readDb)
        {
            await using var handle = await scope.BeginAsync();
            var run = await readDb.AgentRuns.AsNoTracking()
                .FirstAsync(r => r.Id == runId);
            Assert.Equal(RunStatus.Done, run.Status);
        }
    }

    [Fact]
    public async Task ResumeRun_twice_is_idempotent_no_duplicate_checkpoint()
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA);

        var (execDb, execJob) = _fixture.CreateExecuteRunJob(_fixture.BrandA);
        await using (execDb) { await execJob.ExecuteAsync(runId, _fixture.BrandA); }

        // Approve: transitions AwaitingApproval → Publishing so ResumeRun's guard is satisfied.
        await _fixture.ApproveRunAsync(runId, _fixture.BrandA);

        var (r1Db, r1Job) = _fixture.CreateResumeRunJob(_fixture.BrandA);
        await using (r1Db) { await r1Job.ExecuteAsync(runId, _fixture.BrandA); }

        // Second invocation — status is now Done so the Publishing-only guard returns early
        var (r2Db, r2Job) = _fixture.CreateResumeRunJob(_fixture.BrandA);
        await using (r2Db) { await r2Job.ExecuteAsync(runId, _fixture.BrandA); }

        var (readDb, scope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (readDb)
        {
            await using var handle = await scope.BeginAsync();

            var run = await readDb.AgentRuns.AsNoTracking()
                .FirstAsync(r => r.Id == runId);
            Assert.Equal(RunStatus.Done, run.Status);

            var checkpoints = await readDb.RunCheckpoints.AsNoTracking()
                .Where(c => c.AgentRunId == runId)
                .ToListAsync();
            Assert.Single(checkpoints);
        }
    }

    [Fact]
    public async Task Rejected_run_is_terminal_and_ResumeRun_is_a_noop()
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA, RunStatus.Rejected);

        var (resumeDb, resumeJob) = _fixture.CreateResumeRunJob(_fixture.BrandA);
        await using (resumeDb) { await resumeJob.ExecuteAsync(runId, _fixture.BrandA); }

        var (readDb, scope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (readDb)
        {
            await using var handle = await scope.BeginAsync();
            var run = await readDb.AgentRuns.AsNoTracking()
                .FirstAsync(r => r.Id == runId);
            Assert.Equal(RunStatus.Rejected, run.Status);
        }
    }

    [Fact]
    [Trait("Category", "Isolation")]
    public async Task Worker_scoped_to_brand_A_cannot_read_brand_B_run()
    {
        var brandBRunId = await _fixture.SeedAgentRunAsync(_fixture.BrandB);

        var (db, scope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            var brandBRun = await db.AgentRuns.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == brandBRunId);
            Assert.Null(brandBRun);
        }
    }
}
