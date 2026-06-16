using System.Text.Json;
using Backend.Core.Domain;
using Backend.Core.Multitenancy;
using Backend.Core.Orchestration;
using Backend.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Backend.Infrastructure.Jobs;

/// <summary>
/// The regenerate re-entry segment (DL-036). Enqueued by the gate when a reviewer regenerates: the
/// endpoint has already recorded the <c>ApprovalAction</c> and transitioned <c>AwaitingApproval →
/// Running</c>. This job rehydrates the checkpoint and re-runs the rewind → Creative Director → Media
/// graph (NO Strategist), returning a fresh Draft to <c>AwaitingApproval</c> (or <c>Failed</c> on a
/// fatal node error — e.g. the in-graph cost-ceiling breach). Mirrors <see cref="ExecuteRunJob"/>'s
/// single brand-scope unit (the regen graph's RLS-scoped RAG reads need the GUC; no coordinator here).
/// </summary>
public sealed class RegenerateRunJob
{
    private readonly AppDbContext _db;
    private readonly IBrandScope _scope;
    private readonly IBrandContext _brandContext;
    private readonly IOrchestrator _orchestrator;

    public RegenerateRunJob(
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

    public async Task ExecuteAsync(
        Guid runId, Guid brandId, RegenerateMode mode, CancellationToken cancellationToken = default)
    {
        _brandContext.Bind(brandId);
        await using var handle = await _scope.BeginAsync(cancellationToken);

        var run = await _db.AgentRuns
            .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null)
        {
            return;
        }

        // Backstop: a regenerate re-entry runs only on the Running state the endpoint set (guards a
        // stray/duplicate fire). Anything else no-ops.
        if (run.Status != RunStatus.Running)
        {
            return;
        }

        var checkpoint = await _db.RunCheckpoints
            .FirstOrDefaultAsync(c => c.AgentRunId == runId, cancellationToken);
        if (checkpoint is null)
        {
            return;
        }

        var state = JsonSerializer.Deserialize<RunState>(checkpoint.StateJson, RunStateJsonOptions.Options)!;
        state = await _orchestrator.RunRegenerateAsync(state, mode, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        checkpoint.StateJson = JsonSerializer.Serialize(state, RunStateJsonOptions.Options);

        // A fatal node failure (CD/Media exhaustion, ceiling breach) fails the run; otherwise it
        // returns to the human gate with the regenerated Draft (DL-022/036).
        run.TransitionTo(state.FatalError is not null ? RunStatus.Failed : RunStatus.AwaitingApproval, now);

        await _db.SaveChangesAsync(cancellationToken);
        await handle.CompleteAsync(cancellationToken);
    }
}
