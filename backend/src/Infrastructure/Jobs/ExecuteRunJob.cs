using System.Text.Json;
using Backend.Core.Domain;
using Backend.Core.Multitenancy;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Backend.Infrastructure.Persistence;
using Backend.Infrastructure.Tracing;
using Microsoft.EntityFrameworkCore;

namespace Backend.Infrastructure.Jobs;

public sealed class ExecuteRunJob
{
    private readonly AppDbContext _db;
    private readonly IBrandScope _scope;
    private readonly IBrandContext _brandContext;
    private readonly IOrchestrator _orchestrator;

    public ExecuteRunJob(
        AppDbContext db,
        IBrandScope scope,
        IBrandContext brandContext,
        IOrchestrator orchestrator)
    {
        _db = db;
        _scope = scope;
        _brandContext = brandContext;
        _orchestrator = orchestrator;
    }

    public async Task ExecuteAsync(Guid runId, Guid brandId, CancellationToken cancellationToken = default)
    {
        _brandContext.Bind(brandId);

        // Bind the ambient run-trace context so every LLM call in this segment records a generation on
        // this run's trace (the wrapping LangfuseChatClient reads it). ExecuteRun makes the LLM calls.
        using var traceScope = RunTraceScope.Begin(runId, brandId);

        await using var handle = await _scope.BeginAsync(cancellationToken);

        var run = await _db.AgentRuns
            .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);

        if (run is null)
        {
            return;
        }

        if (run.Status is RunStatus.AwaitingApproval
                        or RunStatus.Publishing
                        or RunStatus.Done
                        or RunStatus.Failed
                        or RunStatus.Rejected)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        run.TransitionTo(RunStatus.Running, now);

        // The brand's structured pillars (the Strategist's validation contract, R7) and the run's
        // target surface (the aspect-ratio stamp + Copywriting/Media constraints) are readable run
        // inputs. The profile read is RLS-scoped (no manual brand WHERE).
        var profile = await _db.BrandProfiles.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        IReadOnlyList<string> pillars = profile?.ContentPillars ?? [];

        // Per-run modality (DL-058 Decision 1) is read from the persisted AgentRun row — NOT the job
        // payload (DL-006) — so a retry rebuilds the same modality. A video run targets the reel surface
        // (9:16, DL-030); image runs stay on the feed surface, exactly as before.
        var isVideo = run.Modality == Modality.Video;
        var modality = isVideo ? "video" : "image";
        var surface = isVideo ? "instagram_reel" : "instagram_feed";
        var videoSource = run.VideoSource ?? VideoSource.ImageSeed;

        var state = new RunState(
            RunId: runId,
            BrandId: brandId,
            Phase: GraphPhase.Strategy,
            Strategy: null,
            Creative: null,
            Caption: null,
            Media: null,
            Draft: null,
            Approval: null,
            Publish: null,
            // Flat per-run media budget provision (the existing image seed): a video run needs headroom
            // over a paid Veo clip (Media:VideoPricePerSec × duration), so it gets a larger flat budget.
            // The per-second PRICE stays config-bound (DL-029); this is only the gate's spend ceiling.
            Budget: new Budget(TokenBudget: 10_000, TokensSpent: 0, MediaBudget: isVideo ? 5.00m : 1.00m, MediaSpent: 0m),
            Errors: [],
            Trace: new TraceRefs(TraceId: string.Empty, SpanIds: [], Spans: []),
            TargetSurface: surface,
            ContentPillars: pillars,
            Candidates: null,
            IncurredCosts: [],
            FatalError: null,
            Modality: modality,
            VideoSource: videoSource);

        state = await _orchestrator.RunGenerationAsync(state, cancellationToken);

        var json = JsonSerializer.Serialize(state, RunStateJsonOptions.Options);
        var existing = await _db.RunCheckpoints
            .FirstOrDefaultAsync(c => c.AgentRunId == runId, cancellationToken);

        if (existing is not null)
        {
            existing.StateJson = json;
        }
        else
        {
            _db.RunCheckpoints.Add(new RunCheckpoint
            {
                Id = Guid.NewGuid(),
                BrandId = brandId,
                AgentRunId = runId,
                StateJson = json,
                CreatedAt = now,
            });
        }

        // A fatal node failure (Strategist/CD/selection exhaustion, global ceiling, Gemini fail) fails
        // the run; otherwise it reaches the human gate. The checkpoint is written either way so the
        // trace and errors are visible (DL-022/023).
        run.TransitionTo(
            state.FatalError is not null ? RunStatus.Failed : RunStatus.AwaitingApproval,
            now);

        await _db.SaveChangesAsync(cancellationToken);
        await handle.CompleteAsync(cancellationToken);
    }
}
