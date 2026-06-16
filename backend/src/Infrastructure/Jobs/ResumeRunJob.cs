using System.Text.Json;
using Backend.Core.Domain;
using Backend.Core.Multitenancy;
using Backend.Core.Orchestration;
using Backend.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Backend.Infrastructure.Jobs;

public sealed class ResumeRunJob
{
    private readonly AppDbContext _db;
    private readonly IBrandScope _scope;
    private readonly IBrandContext _brandContext;
    private readonly IOrchestrator _orchestrator;

    public ResumeRunJob(
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

        // The scheduled delayed job fires with the run still Scheduled — advance it to Publishing
        // before publishing (DL-037). Approve-now arrives already Publishing. Any other status
        // (e.g. Cancelled, already Done) no-ops. The full resumable-state pre-check is Slice 4.
        if (run.Status == RunStatus.Scheduled)
        {
            run.TransitionTo(RunStatus.Publishing, DateTimeOffset.UtcNow);
        }
        else if (run.Status != RunStatus.Publishing)
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

        state = await _orchestrator.RunPublishAsync(state, cancellationToken);

        var now = DateTimeOffset.UtcNow;

        checkpoint.StateJson = JsonSerializer.Serialize(state, RunStateJsonOptions.Options);

        run.TransitionTo(RunStatus.Done, now);

        await _db.SaveChangesAsync(cancellationToken);
        await handle.CompleteAsync(cancellationToken);
    }
}
