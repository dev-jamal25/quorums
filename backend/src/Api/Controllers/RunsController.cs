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
            return Conflict(new { error = $"Run is in status {run.Status} and cannot be approved/rejected." });
        }

        var now = DateTimeOffset.UtcNow;
        var isApprove = string.Equals(request.Decision, "approve", StringComparison.OrdinalIgnoreCase);

        _db.ApprovalActions.Add(new ApprovalAction
        {
            Id = Guid.NewGuid(),
            BrandId = brandId,
            AgentRunId = id,
            Action = isApprove ? ApprovalActionType.Approve : ApprovalActionType.Reject,
            Actor = "human",
            OccurredAt = now,
        });

        if (isApprove)
        {
            run.TransitionTo(RunStatus.Publishing, now);
            await _db.SaveChangesAsync(cancellationToken);
            await handle.CompleteAsync(cancellationToken);

            _jobs.Enqueue<ResumeRunJob>(job => job.ExecuteAsync(id, brandId, CancellationToken.None));
        }
        else
        {
            run.TransitionTo(RunStatus.Rejected, now);
            await _db.SaveChangesAsync(cancellationToken);
            await handle.CompleteAsync(cancellationToken);
        }

        return Ok();
    }
}
