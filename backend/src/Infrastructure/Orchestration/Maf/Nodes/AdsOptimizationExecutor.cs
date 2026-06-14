using Backend.Core.Orchestration;
using Microsoft.Agents.AI.Workflows;

namespace Backend.Infrastructure.Orchestration.Maf.Nodes;

/// <summary>
/// Ads Optimization — a designed-for stub (DL-019). Present in the codebase and constructible,
/// but off the MVP active path; the spine never routes to it. Returns a not-implemented marker
/// as a structured <see cref="ToolError"/> rather than throwing, so it is always safe to wire
/// in. It is stubbed, not invented and not cut — the advanced ads path slots in here later.
/// </summary>
public sealed class AdsOptimizationExecutor : Executor<RunState, RunState>
{
    public AdsOptimizationExecutor()
        : base("ads-optimization")
    {
    }

    public override ValueTask<RunState> HandleAsync(
        RunState message, IWorkflowContext context, CancellationToken cancellationToken = default)
        => new(message with
        {
            Errors = [.. message.Errors, new ToolError(
                Code: "ads.not_implemented",
                Message: "Ads Optimization is an advanced-scope stub (DL-019).",
                Retryable: false)],
        });
}
