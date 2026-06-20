using System.Text.Json;
using Backend.Api.Dtos;
using Backend.Core.Domain;
using Backend.Core.Multitenancy;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Backend.Core.Storage;
using Backend.Infrastructure.Configuration.Options;
using Backend.Infrastructure.Jobs;
using Backend.Infrastructure.Persistence;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Backend.Api.Controllers;

[ApiController]
[Route("runs")]
public sealed class RunsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IBrandScope _scope;
    private readonly IBrandContext _brandContext;
    private readonly IBackgroundJobClient _jobs;
    private readonly IStorageService _storage;
    private readonly RegenerationOptions _regeneration;

    public RunsController(
        AppDbContext db,
        IBrandScope scope,
        IBrandContext brandContext,
        IBackgroundJobClient jobs,
        IStorageService storage,
        IOptions<RegenerationOptions> regeneration)
    {
        _db = db;
        _scope = scope;
        _brandContext = brandContext;
        _jobs = jobs;
        _storage = storage;
        _regeneration = regeneration.Value;
    }

    [HttpPost]
    [ProducesResponseType(typeof(CreateRunResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CreateRunResponse>> Create(
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CreateRunRequest? request,
        CancellationToken cancellationToken)
    {
        if (!_brandContext.HasBrand)
        {
            return BadRequest(new { error = "X-Brand-Id header is required." });
        }

        var brandId = _brandContext.RequireBrandId();

        // Resolve the per-run modality (DL-058): no body / no modality → Image (no regression). A Video run
        // defaults videoSource to ImageSeed; an Image run never carries one (the validator rejects it).
        var modality = request?.Modality ?? Modality.Image;
        var videoSource = modality == Modality.Video
            ? request?.VideoSource ?? VideoSource.ImageSeed
            : (VideoSource?)null;

        await using var handle = await _scope.BeginAsync(cancellationToken);

        var run = new AgentRun
        {
            Id = Guid.NewGuid(),
            BrandId = brandId,
            Status = RunStatus.Queued,
            Modality = modality,
            VideoSource = videoSource,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _db.AgentRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken);
        await handle.CompleteAsync(cancellationToken);

        // DL-006: the payload is runId only — modality travels through the AgentRun row, so a retry rebuilds it.
        _jobs.Enqueue<ExecuteRunJob>(job => job.ExecuteAsync(run.Id, brandId, CancellationToken.None));

        return Accepted($"/runs/{run.Id}", new CreateRunResponse(run.Id, modality, videoSource));
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<RunSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<RunSummaryDto>>> List(CancellationToken cancellationToken)
    {
        if (!_brandContext.HasBrand)
        {
            return BadRequest(new { error = "X-Brand-Id header is required." });
        }

        await using var handle = await _scope.BeginAsync(cancellationToken);

        // RLS-scoped: a brand lists only its own runs. Newest first for the dashboard.
        var runs = await _db.AgentRuns.AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new RunSummaryDto(r.Id, r.Status, r.CreatedAt, r.UpdatedAt))
            .ToListAsync(cancellationToken);

        await handle.CompleteAsync(cancellationToken);
        return Ok(runs);
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
        return Ok(new RunStatusResponse(run.Id, run.Status, phase, run.Modality, run.VideoSource));
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

    [HttpGet("{id:guid}/review")]
    [ProducesResponseType(typeof(RunReviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RunReviewDto>> Review(Guid id, CancellationToken cancellationToken)
    {
        if (!_brandContext.HasBrand)
        {
            return BadRequest(new { error = "X-Brand-Id header is required." });
        }

        await using var handle = await _scope.BeginAsync(cancellationToken);

        // Everything assembled under the one RLS-bound scope: the run, its checkpoint, the gate history,
        // and the publish outcome. A brand can only project its own run.
        var run = await _db.AgentRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (run is null)
        {
            await handle.CompleteAsync(cancellationToken);
            return NotFound();
        }

        var checkpoint = await _db.RunCheckpoints.AsNoTracking()
            .FirstOrDefaultAsync(c => c.AgentRunId == id, cancellationToken);
        var state = checkpoint is null
            ? null
            : JsonSerializer.Deserialize<RunState>(checkpoint.StateJson, RunStateJsonOptions.Options);

        var actions = await _db.ApprovalActions.AsNoTracking()
            .Where(a => a.AgentRunId == id)
            .ToListAsync(cancellationToken);

        var publish = await _db.PublishRecords.AsNoTracking()
            .FirstOrDefaultAsync(p => p.AgentRunId == id, cancellationToken);

        await handle.CompleteAsync(cancellationToken);

        // Pure projection; the available-actions list comes from the same GateActionPolicy the gate
        // endpoints enforce (no second copy of the state machine).
        return Ok(RunReviewProjection.From(run, state, actions, publish, _regeneration.MaxPerRun));
    }

    [HttpGet("{id:guid}/media")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Media(Guid id, CancellationToken cancellationToken)
    {
        if (!_brandContext.HasBrand)
        {
            return BadRequest(new { error = "X-Brand-Id header is required." });
        }

        string? key;
        string? declaredMimeType;
        await using (var handle = await _scope.BeginAsync(cancellationToken))
        {
            // Resolve the storage key from the brand's own RLS-scoped checkpoint — never a
            // caller-supplied key — so the proxied object can only be this brand's asset.
            var run = await _db.AgentRuns.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
            if (run is null)
            {
                await handle.CompleteAsync(cancellationToken);
                return NotFound();
            }

            var checkpoint = await _db.RunCheckpoints.AsNoTracking()
                .FirstOrDefaultAsync(c => c.AgentRunId == id, cancellationToken);
            var state = checkpoint is null
                ? null
                : JsonSerializer.Deserialize<RunState>(checkpoint.StateJson, RunStateJsonOptions.Options);

            var mediaRef = state?.Draft?.MediaRef ?? state?.Media;
            key = mediaRef?.StorageKey;
            declaredMimeType = mediaRef?.MimeType;
            await handle.CompleteAsync(cancellationToken);
        }

        if (key is null)
        {
            return NotFound();
        }

        var media = await _storage.GetAsync(key, cancellationToken);
        if (media is null)
        {
            return NotFound();
        }

        // File() throws on a null/empty content type. Prefer the stored type, fall back to the asset's
        // declared MimeType, then a safe generic — so a stored object with no content type renders
        // instead of throwing a 500 (which the browser would mask as a CORS error).
        var contentType = !string.IsNullOrWhiteSpace(media.ContentType) ? media.ContentType
            : !string.IsNullOrWhiteSpace(declaredMimeType) ? declaredMimeType
            : "application/octet-stream";
        return File(media.Content, contentType);
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
            return Conflict(new { error = $"Run is in status {run.Status}; the gate requires AwaitingApproval." });
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

        if (request.Decision == GateDecision.Regenerate)
        {
            // Hard per-run bound (DL-036): count prior regenerate decisions (RLS-scoped) and ask the
            // SAME policy the review DTO surfaces whether another is allowed. Over the limit → block
            // with NO graph re-entry, NO row, status unchanged.
            var regenCount = await _db.ApprovalActions
                .CountAsync(a => a.AgentRunId == id && a.Action == ApprovalActionType.Regenerate, cancellationToken);
            if (!GateActionPolicy.Allows(GateAction.Regenerate, run.Status, regenCount, _regeneration.MaxPerRun))
            {
                return Conflict(new { error = $"Regenerate limit reached ({_regeneration.MaxPerRun}) for this run." });
            }

            // The validator guarantees Mode is present and one of the kebab values.
            var mode = request.Mode == RegenerateModes.ReselectAngle
                ? RegenerateMode.ReselectAngle
                : RegenerateMode.SameAngle;

            var regenerate = NewAction(id, brandId, ApprovalActionType.Regenerate, now, reason: request.Reason);
            regenerate.RegenerateMode = request.Mode;
            _db.ApprovalActions.Add(regenerate);

            run.TransitionTo(RunStatus.Running, now); // the AwaitingApproval → Running back-edge (DL-036)
            await _db.SaveChangesAsync(cancellationToken);
            await handle.CompleteAsync(cancellationToken);

            _jobs.Enqueue<RegenerateRunJob>(job => job.ExecuteAsync(id, brandId, mode, CancellationToken.None));
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

        // Cancel is legal only on a Scheduled run (DL-037) — the same rule the review DTO surfaces.
        if (!GateActionPolicy.Allows(GateAction.Cancel, run.Status, regenerateCount: 0, maxRegenerate: 0))
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
