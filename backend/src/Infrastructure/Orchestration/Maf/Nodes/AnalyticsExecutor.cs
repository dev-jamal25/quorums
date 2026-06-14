using Backend.Core.Orchestration;
using Microsoft.Agents.AI.Workflows;

namespace Backend.Infrastructure.Orchestration.Maf.Nodes;

/// <summary>
/// Analytics — a designed-for stub (DL-019). Present in the codebase and constructible, but off
/// the MVP active path; the spine never routes to it. Returns a not-implemented marker as a
/// structured <see cref="ToolError"/> rather than throwing. It is stubbed, not invented and not
/// cut — KPI tracking / performance scoring that feeds the next plan slots in here later.
/// </summary>
public sealed class AnalyticsExecutor : Executor<RunState, RunState>
{
    public AnalyticsExecutor()
        : base("analytics")
    {
    }

    public override ValueTask<RunState> HandleAsync(
        RunState message, IWorkflowContext context, CancellationToken cancellationToken = default)
        => new(message with
        {
            Errors = [.. message.Errors, new ToolError(
                Code: "analytics.not_implemented",
                Message: "Analytics is an advanced-scope stub (DL-019).",
                Retryable: false)],
        });
}
