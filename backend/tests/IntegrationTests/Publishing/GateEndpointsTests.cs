using Backend.Api.Controllers;
using Backend.Api.Dtos;
using Backend.Core.Domain;
using Backend.Core.Storage;
using Backend.Infrastructure.Configuration.Options;
using Backend.IntegrationTests.Durability;
using Backend.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Backend.IntegrationTests.Publishing;

/// <summary>
/// The Slice-3 acceptance gate for the human-gate endpoints (DL-035, DL-037, DL-041). Drives
/// <see cref="RunsController"/> directly over a real RLS-bound Postgres with a recording job client,
/// asserting the decision recording (one append-only <c>ApprovalAction</c> per decision), the
/// state-machine transitions (through the Slice-1 guard), and the Hangfire dispatch (Enqueue /
/// Schedule / Delete). Edit-validation 400 is proven in the validator unit tests.
/// </summary>
[Trait("Category", "Gate")]
[Collection("Durability")]
public sealed class GateEndpointsTests
{
    private static readonly string[] _editedHashtags = ["#edited"];
    private static readonly IOptions<RegenerationOptions> _regen = Options.Create(new RegenerationOptions { MaxPerRun = 3 });
    private static readonly IStorageService _storage = new InMemoryStorageService();

    private readonly DurabilityFixture _fixture;

    public GateEndpointsTests(DurabilityFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Reject_is_terminal_with_no_resume()
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA, RunStatus.AwaitingApproval);
        var jobs = new RecordingBackgroundJobClient();

        var (db, scope, brandContext) = _fixture.CreateGateDeps(_fixture.BrandA);
        await using (db)
        {
            var controller = new RunsController(db, scope, brandContext, jobs, _storage, _regen);
            var result = await controller.Approval(
                runId, new ApprovalRequest(GateDecision.Reject, null, null, "off-brand"), default);
            Assert.IsType<OkResult>(result);
        }

        var (status, _, actions) = await ReadAsync(runId);
        Assert.Equal(RunStatus.Rejected, status);
        var action = Assert.Single(actions);
        Assert.Equal(ApprovalActionType.Reject, action.Action);
        Assert.Equal("off-brand", action.Reason);
        Assert.Equal(0, jobs.EnqueueCount);
    }

    [Fact]
    public async Task Approve_with_edit_publishes_and_leaves_the_draft_unchanged()
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA, RunStatus.AwaitingApproval);
        const string draftJson = "{\"sentinel\":\"original-ai-draft\"}";
        await _fixture.SeedCheckpointAsync(runId, _fixture.BrandA, draftJson);
        var jobs = new RecordingBackgroundJobClient();

        var (db, scope, brandContext) = _fixture.CreateGateDeps(_fixture.BrandA);
        await using (db)
        {
            var controller = new RunsController(db, scope, brandContext, jobs, _storage, _regen);
            var request = new ApprovalRequest(
                GateDecision.Approve, new ApprovalEdits("edited caption", _editedHashtags), null, null);
            var result = await controller.Approval(runId, request, default);
            Assert.IsType<OkResult>(result);
        }

        var (status, _, actions) = await ReadAsync(runId);
        Assert.Equal(RunStatus.Publishing, status);
        var action = Assert.Single(actions);
        Assert.Equal(ApprovalActionType.ApproveWithEdit, action.Action);
        Assert.Equal("edited caption", action.EditedCaption);
        Assert.Equal(_editedHashtags, action.EditedHashtags);
        Assert.Equal(1, jobs.EnqueueCount);

        // The overlay lives on the audit row â€” RunState.Draft (the checkpoint) is byte-identical (DL-035).
        var (rdb, rscope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (rdb)
        {
            await using var handle = await rscope.BeginAsync();
            var checkpoint = await rdb.RunCheckpoints.AsNoTracking().FirstAsync(c => c.AgentRunId == runId);
            Assert.Equal(draftJson, checkpoint.StateJson);
            await handle.CompleteAsync();
        }
    }

    [Fact]
    public async Task Approve_with_schedule_schedules_the_resume_job()
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA, RunStatus.AwaitingApproval);
        var jobs = new RecordingBackgroundJobClient();

        var (db, scope, brandContext) = _fixture.CreateGateDeps(_fixture.BrandA);
        await using (db)
        {
            var controller = new RunsController(db, scope, brandContext, jobs, _storage, _regen);
            var request = new ApprovalRequest(
                GateDecision.Approve, null, DateTimeOffset.UtcNow.AddHours(2), null);
            var result = await controller.Approval(runId, request, default);
            Assert.IsType<OkResult>(result);
        }

        var (status, scheduledJobId, actions) = await ReadAsync(runId);
        Assert.Equal(RunStatus.Scheduled, status);
        var action = Assert.Single(actions);
        Assert.Equal(ApprovalActionType.ApproveWithSchedule, action.Action);
        Assert.NotNull(action.ScheduledFor);
        Assert.Equal(1, jobs.ScheduleCount);
        Assert.Equal(0, jobs.EnqueueCount);
        Assert.False(string.IsNullOrEmpty(scheduledJobId));
    }

    [Fact]
    public async Task Cancel_on_a_scheduled_run_deletes_the_job_and_terminates()
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA, RunStatus.AwaitingApproval);
        var jobs = new RecordingBackgroundJobClient();

        var (db, scope, brandContext) = _fixture.CreateGateDeps(_fixture.BrandA);
        await using (db)
        {
            var controller = new RunsController(db, scope, brandContext, jobs, _storage, _regen);
            await controller.Approval(
                runId, new ApprovalRequest(GateDecision.Approve, null, DateTimeOffset.UtcNow.AddHours(2), null), default);
            var cancelResult = await controller.Cancel(runId, new CancelRequest("changed my mind"), default);
            Assert.IsType<OkResult>(cancelResult);
        }

        var (status, scheduledJobId, actions) = await ReadAsync(runId);
        Assert.Equal(RunStatus.Cancelled, status);
        Assert.Null(scheduledJobId);
        Assert.Contains(actions, a => a.Action == ApprovalActionType.Cancel && a.Reason == "changed my mind");
        Assert.Single(jobs.DeletedJobIds);
    }

    [Fact]
    public async Task Cancel_on_a_non_scheduled_run_is_a_conflict()
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA, RunStatus.AwaitingApproval);
        var jobs = new RecordingBackgroundJobClient();

        var (db, scope, brandContext) = _fixture.CreateGateDeps(_fixture.BrandA);
        await using (db)
        {
            var controller = new RunsController(db, scope, brandContext, jobs, _storage, _regen);
            var result = await controller.Cancel(runId, new CancelRequest(null), default);
            Assert.IsType<ConflictObjectResult>(result);
        }

        Assert.Empty(jobs.DeletedJobIds);
    }

    private async Task<(RunStatus Status, string? ScheduledJobId, List<ApprovalAction> Actions)> ReadAsync(Guid runId)
    {
        var (db, scope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();
            var run = await db.AgentRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
            var actions = await db.ApprovalActions.AsNoTracking()
                .Where(a => a.AgentRunId == runId).ToListAsync();
            await handle.CompleteAsync();
            return (run.Status, run.ScheduledJobId, actions);
        }
    }
}
