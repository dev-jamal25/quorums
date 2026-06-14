using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Microsoft.Agents.AI.Workflows;

namespace Backend.Infrastructure.Orchestration.Maf.Nodes;

/// <summary>
/// Content Strategist node (DL-019): owns <em>what to say</em> — content pillar, angle,
/// objective, audience. Deterministic stub: returns a canned <see cref="ContentStrategy"/>
/// and records a "strategy" span. Writes only its own <see cref="RunState"/> slice
/// (DL-020). No LLM, no RAG — those arrive in the generation-pipeline slice.
/// </summary>
public sealed class ContentStrategistExecutor : Executor<RunState, RunState>
{
    private readonly ITrace _trace;

    public ContentStrategistExecutor(ITrace trace)
        : base("content-strategist") => _trace = trace;

    public override ValueTask<RunState> HandleAsync(
        RunState message, IWorkflowContext context, CancellationToken cancellationToken = default)
        => new(RunAsync(message, cancellationToken));

    /// <summary>Unit-testable core; the MAF handler is a thin wrapper around it.</summary>
    public async Task<RunState> RunAsync(RunState state, CancellationToken cancellationToken = default)
    {
        var strategy = new ContentStrategy(
            Pillar: "stub-pillar",
            Angle: "stub-angle",
            Objective: "stub-objective",
            Audience: "stub-audience",
            CalendarSlot: null);

        var now = DateTimeOffset.UtcNow;
        var trace = await _trace.RecordAsync(
            state.Trace, state.RunId, state.BrandId, "strategy", null, "ok", now, now, null, cancellationToken)
            .ConfigureAwait(false);

        return state with { Strategy = strategy, Trace = trace };
    }
}
