using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Microsoft.Agents.AI.Workflows;

namespace Backend.Infrastructure.Orchestration.Maf.Nodes;

/// <summary>
/// Supervisor rewind — the regenerate graph's entry node (DL-036). The Supervisor owns
/// <see cref="RunState.Phase"/>, the selected angle (<see cref="RunState.Strategy"/>), and the Draft
/// (DL-020), so the rewind is a Supervisor node, never a side-write from the endpoint/job. It rewinds
/// to <see cref="GraphPhase.Creative"/> and CLEARS the downstream slices
/// (<c>Creative/Caption/Media/Draft</c>) so the CD→Media re-run produces fresh outputs and the
/// order-independent assembly fold (<c>a.Caption ?? b.Caption</c>) picks them. For
/// <see cref="RegenerateMode.ReselectAngle"/> it selects a DIFFERENT banked candidate (DL-027) — no
/// new Strategist call. No span, no tool: it is control-plane state, like <c>supervisor-entry</c>.
/// </summary>
public sealed class SupervisorRewindExecutor : Executor<RunState, RunState>
{
    private readonly RegenerateMode _mode;

    public SupervisorRewindExecutor(RegenerateMode mode)
        : base("supervisor-rewind") => _mode = mode;

    public override ValueTask<RunState> HandleAsync(
        RunState message, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var rewound = message with
        {
            Phase = GraphPhase.Creative,
            Creative = null,
            Caption = null,
            Media = null,
            Draft = null,
        };

        if (_mode == RegenerateMode.ReselectAngle)
        {
            rewound = rewound with { Strategy = ReselectAngle(message) };
        }

        return new(rewound);
    }

    /// <summary>
    /// Cycle to the next banked candidate (wrapping) — always yields a different angle while at least
    /// two candidates exist, so the only bound is the hard per-run regen count (never exhaustion).
    /// Falls back to the current angle when candidates are absent or singular.
    /// </summary>
    private static ContentStrategy? ReselectAngle(RunState state)
    {
        var candidates = state.Candidates?.Candidates;
        if (candidates is null || candidates.Count < 2)
        {
            return state.Strategy;
        }

        var current = -1;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (candidates[i] == state.Strategy)
            {
                current = i;
                break;
            }
        }

        var next = ((current < 0 ? 0 : current) + 1) % candidates.Count;
        return candidates[next];
    }
}
