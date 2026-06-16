using System.Text.Json;
using Backend.Api.Dtos;
using Backend.Core.Domain;
using Backend.Core.Multitenancy;
using Backend.Core.Orchestration;
using Backend.Infrastructure.Jobs;
using Backend.Infrastructure.Persistence;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Backend.Api.Controllers;

[ApiController]
[Route("runs")]
public sealed class RunsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IBrandScope _scope;
    private readonly IBrandContext _brandContext;
    private readonly IBackgroundJobClient _jobs;

    public RunsController(
        AppDbContext db,
        IBrandScope scope,
        IBrandContext brandContext,
        IBackgroundJobClient jobs)
    {
        _db = db;
        _scope = scope;
        _brandContext = brandContext;
        _jobs = jobs;
    }

    [HttpPost]
    [ProducesResponseType(typeof(CreateRunResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CreateRunResponse>> Create(CancellationToken cancellationToken)
    {
        if (!_brandContext.HasBrand)
        {
            return BadRequest(new { error = "X-Brand-Id header is required." });
        }

        var brandId = _brandContext.RequireBrandId();

        await using var handle = await _scope.BeginAsync(cancellationToken);

        var run = new AgentRun
        {
            Id = Guid.NewGuid(),
            BrandId = brandId,
            Status = RunStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _db.AgentRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken);
        await handle.CompleteAsync(cancellationToken);

        _jobs.Enqueue<ExecuteRunJob>(job => job.ExecuteAsync(run.Id, brandId, CancellationToken.None));

        return Accepted($"/runs/{run.Id}", new CreateRunResponse(run.Id));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(RunStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RunStatusResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        if (!_brandContext.HasBrand)
        {
            return BadRequest(new { error = "X-Brand-Id header is required." });
        }

        await using var handle = await _scope.BeginAsync(cancellationToken);

        var run = await _db.AgentRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (run is null)
        {
            await handle.CompleteAsync(cancellationToken);
            return NotFound();
        }

        GraphPhase? phase = null;
        var checkpoint = await _db.RunCheckpoints.AsNoTracking()
            .FirstOrDefaultAsync(c => c.AgentRunId == id, cancellationToken);

        if (checkpoint is not null)
        {
            var state = JsonSerializer.Deserialize<RunState>(
                checkpoint.StateJson, RunStateJsonOptions.Options);
            phase = state?.Phase;
        }

        await handle.CompleteAsync(cancellationToken);
        return Ok(new RunStatusResponse(run.Id, run.Status, phase));
    }

    [HttpGet("{id:guid}/trace")]
    [ProducesResponseType(typeof(RunTraceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RunTraceResponse>> Trace(Guid id, CancellationToken cancellationToken)
    {
        if (!_brandContext.HasBrand)
        {
            return BadRequest(new { error = "X-Brand-Id header is required." });
        }

        await using var handle = await _scope.BeginAsync(cancellationToken);

        // Loaded under the RLS-bound scope: a brand can only read its own run's trace.
        var run = await _db.AgentRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (run is null)
        {
            await handle.CompleteAsync(cancellationToken);
            return NotFound();
        }

        var checkpoint = await _db.RunCheckpoints.AsNoTracking()
            .FirstOrDefaultAsync(c => c.AgentRunId == id, cancellationToken);

        RunState? state = checkpoint is null
            ? null
            : JsonSerializer.Deserialize<RunState>(checkpoint.StateJson, RunStateJsonOptions.Options);

        await handle.CompleteAsync(cancellationToken);

        if (state is null)
        {
            return NotFound();
        }

        var spans = (state.Trace.Spans ?? [])
            .Select(s => new TraceSpanDto(
                s.SpanId,
                s.Node,
                s.Tool,
                s.Status,
                s.StartedAt,
                s.EndedAt,
                (s.EndedAt - s.StartedAt).TotalMilliseconds,
                s.Error))
            .ToList();

        return Ok(new RunTraceResponse(run.Id, state.Trace.TraceId, spans));
    }

    [HttpPost("{id:guid}/approval")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Approval(
        Guid id,
        ApprovalRequest request,
        CancellationToken cancellationToken)
    {
        if (!_brandContext.HasBrand)
        {
            return BadRequest(new { error = "X-Brand-Id header is required." });
        }

        var brandId = _brandContext.RequireBrandId();

        await using var handle = await _scope.BeginAsync(cancellationToken);

        var run = await _db.AgentRuns
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (run is null)
        {
            return NotFound();
        }

        if (run.Status != RunStatus.AwaitingApproval)
        {
            return Conflict(new { error = $"Run is in status {run.Status} and cannot be approved or rejected." });
        }

        var now = DateTimeOffset.UtcNow;

        if (request.Decision == GateDecision.Reject)
        {
            _db.ApprovalActions.Add(NewAction(id, brandId, ApprovalActionType.Reject, now, reason: request.Reason));
            run.TransitionTo(RunStatus.Rejected, now);
            await _db.SaveChangesAsync(cancellationToken);
            await handle.CompleteAsync(cancellationToken);
            return Ok();
        }

        // Approve. The validator has already enforced the edit limits (DL-030) and a future schedule.
        var hasEdits = request.Edits is not null
            && (request.Edits.Caption is not null || request.Edits.Hashtags is not null);

        if (request.ScheduledFor is { } scheduledFor)
        {
            // Schedule (DL-037). The edit overlay still rides on the row for the Slice-4 publish.
            var scheduled = NewAction(id, brandId, ApprovalActionType.ApproveWithSchedule, now);
            scheduled.ScheduledFor = scheduledFor;
            ApplyEdits(scheduled, request.Edits, hasEdits);
            _db.ApprovalActions.Add(scheduled);

            run.TransitionTo(RunStatus.Scheduled, now);

            // Schedule before commit to capture the job id (persisted so cancel can Delete it). If the
            // commit then fails, only an orphan delayed job remains and it no-ops (run stays AwaitingApproval).
            var jobId = _jobs.Schedule<ResumeRunJob>(
                job => job.ExecuteAsync(id, brandId, null, CancellationToken.None),
                scheduledFor - now);
            run.ScheduledJobId = jobId;

            await _db.SaveChangesAsync(cancellationToken);
            await handle.CompleteAsync(cancellationToken);
            return Ok();
        }

        var approve = NewAction(
            id, brandId, hasEdits ? ApprovalActionType.ApproveWithEdit : ApprovalActionType.Approve, now);
        ApplyEdits(approve, request.Edits, hasEdits);
        _db.ApprovalActions.Add(approve);

        run.TransitionTo(RunStatus.Publishing, now);
        await _db.SaveChangesAsync(cancellationToken);
        await handle.CompleteAsync(cancellationToken);

        _jobs.Enqueue<ResumeRunJob>(job => job.ExecuteAsync(id, brandId, null, CancellationToken.None));
        return Ok();
    }

    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(
        Guid id,
        CancelRequest? request,
        CancellationToken cancellationToken)
    {
        if (!_brandContext.HasBrand)
        {
            return BadRequest(new { error = "X-Brand-Id header is required." });
        }

        var brandId = _brandContext.RequireBrandId();

        await using var handle = await _scope.BeginAsync(cancellationToken);

        var run = await _db.AgentRuns
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (run is null)
        {
            return NotFound();
        }

        if (run.Status != RunStatus.Scheduled)
        {
            return Conflict(new { error = $"Run is in status {run.Status}; only a Scheduled run can be cancelled." });
        }

        var now = DateTimeOffset.UtcNow;
        var jobId = run.ScheduledJobId;

        _db.ApprovalActions.Add(NewAction(id, brandId, ApprovalActionType.Cancel, now, reason: request?.Reason));
        run.TransitionTo(RunStatus.Cancelled, now);
        run.ScheduledJobId = null;
        await _db.SaveChangesAsync(cancellationToken);
        await handle.CompleteAsync(cancellationToken);

        // Delete after commit: if it fails, the fired job no-ops (it is no longer Scheduled).
        if (jobId is not null)
        {
            _jobs.Delete(jobId);
        }

        return Ok();
    }

    // The fixed demo principal (DL-040): no identity system in the MVP, captured for the future team drop-in.
    private const string DemoPrincipal = "human";

    private static ApprovalAction NewAction(
        Guid runId, Guid brandId, ApprovalActionType action, DateTimeOffset now, string? reason = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            BrandId = brandId,
            AgentRunId = runId,
            Action = action,
            Actor = DemoPrincipal,
            OccurredAt = now,
            Reason = reason,
        };

    private static void ApplyEdits(ApprovalAction action, ApprovalEdits? edits, bool hasEdits)
    {
        if (!hasEdits || edits is null)
        {
            return;
        }

        action.EditedCaption = edits.Caption;
        action.EditedHashtags = edits.Hashtags?.ToList();
    }
}
