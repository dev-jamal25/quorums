using System.Collections.Frozen;

namespace Backend.Core.Domain;

/// <summary>
/// The single source of truth for legal <see cref="RunStatus"/> edges (DL-006, DL-036, DL-037).
/// Every state change goes through <see cref="AgentRun.TransitionTo"/>, which consults this guard;
/// raw <c>run.Status = …</c> assignments are banned outside initial entity creation (see
/// <c>.claude/rules/orchestration.md</c>). The guard encodes exactly the edges the run pipeline
/// performs plus the Phase-6 additions — nothing speculative.
/// </summary>
public static class RunStatusTransition
{
    // Forward edges of the post lifecycle. An identity edge (from == to) is always allowed and is a
    // no-op re-set — it preserves the idempotent ExecuteRun retry that re-sets Running on re-entry.
    private static readonly FrozenDictionary<RunStatus, FrozenSet<RunStatus>> _allowed =
        new Dictionary<RunStatus, FrozenSet<RunStatus>>
        {
            [RunStatus.Queued] = FrozenSet.ToFrozenSet([RunStatus.Running]),
            [RunStatus.Running] = FrozenSet.ToFrozenSet([RunStatus.AwaitingApproval, RunStatus.Failed]),
            // AwaitingApproval -> Running is the regenerate back-edge (DL-036); the gate is re-entrant.
            [RunStatus.AwaitingApproval] = FrozenSet.ToFrozenSet(
                [RunStatus.Publishing, RunStatus.Scheduled, RunStatus.Rejected, RunStatus.Running]),
            [RunStatus.Scheduled] = FrozenSet.ToFrozenSet([RunStatus.Publishing, RunStatus.Cancelled]),
            [RunStatus.Publishing] = FrozenSet.ToFrozenSet([RunStatus.Done]),
            // Done / Failed / Rejected / Cancelled are terminal — no outgoing edges.
        }.ToFrozenDictionary();

    /// <summary>True if <paramref name="to"/> is reachable from <paramref name="from"/> in one step
    /// (or they are equal). Equal is allowed as an idempotent no-op re-set.</summary>
    public static bool IsAllowed(RunStatus from, RunStatus to) =>
        from == to || (_allowed.TryGetValue(from, out var targets) && targets.Contains(to));
}
