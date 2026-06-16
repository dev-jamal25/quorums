using Backend.Core.Domain;

namespace Backend.Core.Orchestration;

/// <summary>
/// The single source of truth for "which gate actions are legal right now" (DL-041). Both the gate
/// endpoints (the regenerate-bound and cancel-scope guards) and the server-computed available-actions
/// list on the review DTO consult THIS — the state machine is not duplicated in a second place. Pure:
/// it derives the action set from the run's <see cref="RunStatus"/> plus the per-run regenerate count
/// against the configured ceiling.
/// </summary>
/// <remarks>
/// Rules (mirror the transitions in <c>state-machine.md</c> and the endpoint guards):
/// <list type="bullet">
///   <item><see cref="GateAction.Approve"/> / <see cref="GateAction.Reject"/> — present only at
///   <see cref="RunStatus.AwaitingApproval"/> (the gate).</item>
///   <item><see cref="GateAction.Regenerate"/> — present at <see cref="RunStatus.AwaitingApproval"/>
///   AND while the per-run regenerate count is below the ceiling (DL-036); drops once exhausted.</item>
///   <item><see cref="GateAction.Cancel"/> — present only at <see cref="RunStatus.Scheduled"/> (a run
///   already past the gate, waiting for its slot, DL-037).</item>
///   <item>Every other status (Queued / Running / Publishing / Done / Failed / Rejected / Cancelled)
///   exposes no action — the run is in-flight or terminal.</item>
/// </list>
/// </remarks>
public static class GateActionPolicy
{
    /// <summary>The actions legal for a run in <paramref name="status"/> given its regenerate budget.</summary>
    public static IReadOnlyList<GateAction> Available(RunStatus status, int regenerateCount, int maxRegenerate) =>
        status switch
        {
            RunStatus.AwaitingApproval when RegenerateAllowed(status, regenerateCount, maxRegenerate) =>
                [GateAction.Approve, GateAction.Regenerate, GateAction.Reject],
            RunStatus.AwaitingApproval =>
                [GateAction.Approve, GateAction.Reject],
            RunStatus.Scheduled =>
                [GateAction.Cancel],
            _ => [],
        };

    /// <summary>True when <paramref name="action"/> is legal now — the guard the endpoints share.</summary>
    public static bool Allows(GateAction action, RunStatus status, int regenerateCount, int maxRegenerate) =>
        Available(status, regenerateCount, maxRegenerate).Contains(action);

    /// <summary>True when a regenerate is legal: at the gate and under the per-run ceiling (DL-036).</summary>
    public static bool RegenerateAllowed(RunStatus status, int regenerateCount, int maxRegenerate) =>
        status == RunStatus.AwaitingApproval && regenerateCount < maxRegenerate;

    /// <summary>How many regenerates remain for the run (never negative).</summary>
    public static int RegenerateRemaining(int regenerateCount, int maxRegenerate) =>
        Math.Max(0, maxRegenerate - regenerateCount);
}
