using System.Text.Json;
using Backend.Core.Domain;
using Backend.Core.Multitenancy;
using Backend.Core.Orchestration;
using Backend.Infrastructure.Persistence;
using Hangfire;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Backend.Infrastructure.Jobs;

/// <summary>
/// The durable resume segment (DL-006, DL-037, DL-039). Structured as discrete brand-scope units so
/// the publish step runs transaction-free (the <c>PublishCoordinator</c> owns its own committed units
/// for crash-safety): a resumable-state pre-check, then the publish graph, then a decision-driven
/// finalize. The run ALWAYS reaches a terminal state — never lingers in <c>Publishing</c>: a
/// transient failure re-throws (bounded Hangfire <c>AutomaticRetry</c>); on the final attempt or a
/// terminal failure it sets <c>Failed</c> and returns normally.
/// </summary>
public sealed partial class ResumeRunJob
{
    // Mirrors the AutomaticRetry budget below: the final allotted attempt is RetryCount == MaxAttempts.
    private const int MaxAttempts = 3;

    private readonly AppDbContext _db;
    private readonly IBrandScope _scope;
    private readonly IBrandContext _brandContext;
    private readonly IOrchestrator _orchestrator;
    private readonly ILogger<ResumeRunJob> _logger;

    public ResumeRunJob(
        AppDbContext db,
        IBrandScope scope,
        IBrandContext brandContext,
        IOrchestrator orchestrator,
        ILogger<ResumeRunJob> logger)
    {
        _db = db;
        _scope = scope;
        _brandContext = brandContext;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    /// <summary>The Hangfire entry point. Reads the retry attempt from the job context once and delegates.</summary>
    [AutomaticRetry(Attempts = MaxAttempts, DelaysInSeconds = new[] { 10, 30, 90 })]
    public Task ExecuteAsync(
        Guid runId, Guid brandId, PerformContext? context, CancellationToken cancellationToken = default)
        => ExecuteAsync(runId, brandId, context?.GetJobParameter<int>("RetryCount") ?? 0, cancellationToken);

    /// <summary>Attempt-explicit core (internal so tests drive the retry sequence without Hangfire).</summary>
    internal async Task ExecuteAsync(
        Guid runId, Guid brandId, int attempt = 0, CancellationToken cancellationToken = default)
    {
        _brandContext.Bind(brandId);

        var state = await PrepareAsync(runId, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return;
        }

        // The node + coordinator own their brand-scope units — NO ambient transaction here.
        state = await _orchestrator.RunPublishAsync(state, cancellationToken).ConfigureAwait(false);

        await FinalizeAsync(runId, state, attempt, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resumable-state backstop + checkpoint load (committed unit). Returns null to no-op.</summary>
    private async Task<RunState?> PrepareAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var handle = await _scope.BeginAsync(cancellationToken).ConfigureAwait(false);

        var run = await _db.AgentRuns.FirstOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return null;
        }

        switch (run.Status)
        {
            case RunStatus.Scheduled:
                run.TransitionTo(RunStatus.Publishing, DateTimeOffset.UtcNow); // delayed-fire (DL-037)
                break;
            case RunStatus.Publishing:
                break;
            default:
                // Cancelled / Rejected / Done / Failed: guards the cancel→Delete race and Hangfire's
                // at-least-once delivery. No publish, no throw, no transition.
                LogNotResumable(runId, run.Status);
                await handle.CompleteAsync(cancellationToken).ConfigureAwait(false);
                return null;
        }

        var checkpoint = await _db.RunCheckpoints
            .FirstOrDefaultAsync(c => c.AgentRunId == runId, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null)
        {
            LogNoCheckpoint(runId);
            await handle.CompleteAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false); // persist Scheduled -> Publishing
        await handle.CompleteAsync(cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Deserialize<RunState>(checkpoint.StateJson, RunStateJsonOptions.Options)!;
    }

    /// <summary>Map the publish result to a terminal state or a retry signal (committed unit).</summary>
    private async Task FinalizeAsync(Guid runId, RunState state, int attempt, CancellationToken cancellationToken)
    {
        var decision = PublishResumeDecision.Decide(state.Publish, attempt, MaxAttempts);
        var now = DateTimeOffset.UtcNow;

        await using (var handle = await _scope.BeginAsync(cancellationToken).ConfigureAwait(false))
        {
            var run = await _db.AgentRuns.FirstOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
            var checkpoint = await _db.RunCheckpoints
                .FirstOrDefaultAsync(c => c.AgentRunId == runId, cancellationToken).ConfigureAwait(false);
            if (run is null || checkpoint is null)
            {
                await handle.CompleteAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            checkpoint.StateJson = JsonSerializer.Serialize(state, RunStateJsonOptions.Options);

            switch (decision.Outcome)
            {
                case PublishResumeOutcome.Complete:
                    run.TransitionTo(RunStatus.Done, now);
                    break;
                case PublishResumeOutcome.Fail:
                    run.TransitionTo(RunStatus.Failed, now);
                    break;
                case PublishResumeOutcome.Retry:
                default:
                    break; // stays Publishing; the throw below triggers AutomaticRetry.
            }

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await handle.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        if (decision.Outcome == PublishResumeOutcome.Retry)
        {
            throw new TransientPublishException(decision.FailureReason ?? "Transient publish failure; retrying.");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "ResumeRun no-op: run {RunId} is {Status}, not resumable.")]
    private partial void LogNotResumable(Guid runId, RunStatus status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "ResumeRun no-op: run {RunId} has no checkpoint.")]
    private partial void LogNoCheckpoint(Guid runId);
}
