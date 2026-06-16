using System.Text.Json;
using Backend.Core.Domain;
using Backend.Core.Multitenancy;
using Backend.Core.Orchestration;
using Backend.Infrastructure.Persistence;
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
        run.Status = RunStatus.Running;
        run.UpdatedAt = now;

        // The brand's structured pillars (the Strategist's validation contract, R7) and the run's
        // target surface (the aspect-ratio stamp + Copywriting/Media constraints) are readable run
        // inputs. The profile read is RLS-scoped (no manual brand WHERE).
        var profile = await _db.BrandProfiles.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        IReadOnlyList<string> pillars = profile?.ContentPillars ?? [];

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
            Budget: new Budget(TokenBudget: 10_000, TokensSpent: 0, MediaBudget: 1.00m, MediaSpent: 0m),
            Errors: [],
            Trace: new TraceRefs(TraceId: string.Empty, SpanIds: [], Spans: []),
            TargetSurface: "instagram_feed",
            ContentPillars: pillars,
            Candidates: null,
            IncurredCosts: [],
            FatalError: null);

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
        run.Status = state.FatalError is not null ? RunStatus.Failed : RunStatus.AwaitingApproval;
        run.UpdatedAt = now;

        await _db.SaveChangesAsync(cancellationToken);
        await handle.CompleteAsync(cancellationToken);
    }
}
