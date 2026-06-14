using Backend.Core.Orchestration;
using Microsoft.Agents.AI.Workflows;

namespace Backend.Infrastructure.Orchestration.Maf.Nodes;

/// <summary>
/// Supervisor entry node — the graph's start executor. The Supervisor is the sole writer of
/// <see cref="RunState.Phase"/> (DL-020); here it sets the phase to <c>Strategy</c> and
/// forwards. It is a routing/state node, not an agent, so it records no span and calls no
/// tool. Brand scope and the durable wait are owned outside the graph (the job + state
/// machine, DL-018); the budget check before the Media node is a no-op in this slice
/// (cost model is out of scope).
/// </summary>
public sealed class SupervisorEntryExecutor : Executor<RunState, RunState>
{
    public SupervisorEntryExecutor()
        : base("supervisor-entry")
    {
    }

    public override ValueTask<RunState> HandleAsync(
        RunState message, IWorkflowContext context, CancellationToken cancellationToken = default)
        => new(message with { Phase = GraphPhase.Strategy });
}
