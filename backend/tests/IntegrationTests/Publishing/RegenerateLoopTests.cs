using Backend.Api.Controllers;
using Backend.Api.Dtos;
using Backend.Core.Domain;
using Backend.Core.Orchestration;
using Backend.Core.Storage;
using Backend.Infrastructure.Configuration.Options;
using Backend.Infrastructure.Integrations.Meta;
using Backend.IntegrationTests.Durability;
using Backend.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Backend.IntegrationTests.Publishing;

/// <summary>
/// The Slice-5 acceptance gate for the regenerate loop (DL-036). Drives the gate endpoint + the
/// <c>RegenerateRunJob</c> over a real RLS-bound Postgres: the bounded decision, the
/// <c>AwaitingApproval → Running</c> back-edge, the Supervisor rewind (same-angle keeps the angle,
/// reselect-angle picks a different banked one — no Strategist re-run), and re-entrancy (a later
/// approve publishes the REGENERATED Draft, tying into Slice 4).
/// </summary>
[Trait("Category", "Regenerate")]
[Collection("Durability")]
public sealed class RegenerateLoopTests
{
    private static readonly IOptions<RegenerationOptions> _maxThree = Options.Create(new RegenerationOptions { MaxPerRun = 3 });
    private static readonly IStorageService _storage = new InMemoryStorageService();

    private readonly DurabilityFixture _fixture;

    public RegenerateLoopTests(DurabilityFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Regenerate_records_the_action_takes_the_back_edge_and_dispatches()
    {
        var runId = await GenerateAsync();
        var jobs = new RecordingBackgroundJobClient();

        var (db, scope, brandContext) = _fixture.CreateGateDeps(_fixture.BrandA);
        await using (db)
        {
            var controller = new RunsController(db, scope, brandContext, jobs, _storage, _maxThree);
            var result = await controller.Approval(
                runId,
                new ApprovalRequest(GateDecision.Regenerate, null, null, "punchier please", RegenerateModes.SameAngle),
                default);
            Assert.IsType<OkResult>(result);
        }

        Assert.Equal(RunStatus.Running, await _fixture.ReadRunStatusAsync(runId, _fixture.BrandA));
        var regen = Assert.Single(await ReadActionsAsync(runId, ApprovalActionType.Regenerate));
        Assert.Equal(RegenerateModes.SameAngle, regen.RegenerateMode);
        Assert.Equal("punchier please", regen.Reason);
        Assert.Equal(1, jobs.EnqueueCount);
    }

    [Fact]
    public async Task Regenerate_past_the_bound_is_blocked_with_no_re_entry()
    {
        var runId = await GenerateAsync();
        await SeedRegenerateActionsAsync(runId, 2); // already at the limit
        var jobs = new RecordingBackgroundJobClient();
        var maxTwo = Options.Create(new RegenerationOptions { MaxPerRun = 2 });

        var (db, scope, brandContext) = _fixture.CreateGateDeps(_fixture.BrandA);
        await using (db)
        {
            var controller = new RunsController(db, scope, brandContext, jobs, _storage, maxTwo);
            var result = await controller.Approval(
                runId,
                new ApprovalRequest(GateDecision.Regenerate, null, null, null, RegenerateModes.SameAngle),
                default);
            Assert.IsType<ConflictObjectResult>(result);
        }

        Assert.Equal(RunStatus.AwaitingApproval, await _fixture.ReadRunStatusAsync(runId, _fixture.BrandA)); // unchanged
        Assert.Equal(2, (await ReadActionsAsync(runId, ApprovalActionType.Regenerate)).Count); // no third row
        Assert.Equal(0, jobs.EnqueueCount); // no graph re-entry
    }

    [Fact]
    public async Task Same_angle_re_runs_creative_without_reselecting_or_re_running_the_strategist()
    {
        var runId = await GenerateAsync();
        var before = await _fixture.ReadCheckpointStateAsync(runId, _fixture.BrandA);

        await RegenerateAsync(runId, RegenerateMode.SameAngle, RegenerateModes.SameAngle);

        Assert.Equal(RunStatus.AwaitingApproval, await _fixture.ReadRunStatusAsync(runId, _fixture.BrandA));
        var after = await _fixture.ReadCheckpointStateAsync(runId, _fixture.BrandA);
        Assert.NotNull(after!.Draft);
        // SAME selected angle (compare the distinguishing fields — record == is reference-based on the
        // Grounding list member, which differs after a JSON round-trip).
        Assert.Equal(before!.Strategy!.Angle, after.Strategy!.Angle);
        Assert.Equal(before.Strategy.Pillar, after.Strategy.Pillar);
        Assert.Equal(1, after.Trace.Spans.Count(s => s.Node == "strategy" && s.Tool is null)); // Strategist lifecycle span (excludes the DL-054 provenance span) — not re-invoked
        Assert.Equal(1, after.Trace.Spans.Count(s => s.Node == "supervisor-selection"));
    }

    [Fact]
    public async Task Reselect_angle_picks_a_different_banked_angle_without_the_strategist()
    {
        var runId = await GenerateAsync();
        var before = await _fixture.ReadCheckpointStateAsync(runId, _fixture.BrandA);

        await RegenerateAsync(runId, RegenerateMode.ReselectAngle, RegenerateModes.ReselectAngle);

        var after = await _fixture.ReadCheckpointStateAsync(runId, _fixture.BrandA);
        Assert.NotNull(after!.Draft);
        Assert.NotEqual(before!.Strategy!.Angle, after.Strategy!.Angle);             // DIFFERENT angle
        Assert.Equal(1, after.Trace.Spans.Count(s => s.Node == "strategy" && s.Tool is null)); // Strategist lifecycle span (excludes the DL-054 provenance span) — not re-invoked
    }

    [Fact]
    public async Task Approve_after_regenerate_publishes_the_regenerated_draft()
    {
        var runId = await GenerateAsync();
        await RegenerateAsync(runId, RegenerateMode.ReselectAngle, RegenerateModes.ReselectAngle);

        var regenerated = await _fixture.ReadCheckpointStateAsync(runId, _fixture.BrandA);
        var expectedCaption = $"{regenerated!.Draft!.CaptionRef.Hook}\n\n{regenerated.Draft.CaptionRef.Body}";

        // Approve the regenerated draft, then run the publish segment with a capturing mock.
        var (gdb, gscope, gbrand) = _fixture.CreateGateDeps(_fixture.BrandA);
        await using (gdb)
        {
            var controller = new RunsController(gdb, gscope, gbrand, new RecordingBackgroundJobClient(), _storage, _maxThree);
            await controller.Approval(runId, new ApprovalRequest(GateDecision.Approve, null, null, null), default);
        }

        var mock = new MockMetaIntegration();
        var (pdb, pjob) = _fixture.CreateResumeRunJob(_fixture.BrandA, mock);
        await using (pdb)
        {
            await pjob.ExecuteAsync(runId, _fixture.BrandA);
        }

        Assert.Equal(RunStatus.Done, await _fixture.ReadRunStatusAsync(runId, _fixture.BrandA));
        Assert.Equal(expectedCaption, mock.LastRequest!.Caption); // the REGENERATED draft, not the original
    }

    // --- helpers -------------------------------------------------------------------------------

    private async Task<Guid> GenerateAsync()
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA);
        var (db, job) = _fixture.CreateExecuteRunJob(_fixture.BrandA);
        await using (db)
        {
            await job.ExecuteAsync(runId, _fixture.BrandA);
        }

        return runId;
    }

    private async Task RegenerateAsync(Guid runId, RegenerateMode mode, string wireMode)
    {
        // Endpoint: records the action + takes the AwaitingApproval → Running back-edge.
        var (gdb, gscope, gbrand) = _fixture.CreateGateDeps(_fixture.BrandA);
        await using (gdb)
        {
            var controller = new RunsController(gdb, gscope, gbrand, new RecordingBackgroundJobClient(), _storage, _maxThree);
            await controller.Approval(
                runId, new ApprovalRequest(GateDecision.Regenerate, null, null, null, wireMode), default);
        }

        // Job: the rewind → CD → Media re-entry.
        var (jdb, job) = _fixture.CreateRegenerateRunJob(_fixture.BrandA);
        await using (jdb)
        {
            await job.ExecuteAsync(runId, _fixture.BrandA, mode);
        }
    }

    private async Task SeedRegenerateActionsAsync(Guid runId, int count)
    {
        var (db, scope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();
            for (var i = 0; i < count; i++)
            {
                db.ApprovalActions.Add(new ApprovalAction
                {
                    Id = Guid.NewGuid(),
                    BrandId = _fixture.BrandA,
                    AgentRunId = runId,
                    Action = ApprovalActionType.Regenerate,
                    Actor = "human",
                    OccurredAt = DateTimeOffset.UtcNow,
                    RegenerateMode = RegenerateModes.SameAngle,
                });
            }

            await db.SaveChangesAsync();
            await handle.CompleteAsync();
        }
    }

    private async Task<List<ApprovalAction>> ReadActionsAsync(Guid runId, ApprovalActionType action)
    {
        var (db, scope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();
            var actions = await db.ApprovalActions.AsNoTracking()
                .Where(a => a.AgentRunId == runId && a.Action == action).ToListAsync();
            await handle.CompleteAsync();
            return actions;
        }
    }
}
