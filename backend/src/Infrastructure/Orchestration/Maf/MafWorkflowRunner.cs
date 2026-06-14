using Backend.Core.Orchestration;
using Microsoft.Agents.AI.Workflows;

namespace Backend.Infrastructure.Orchestration.Maf;

/// <summary>
/// The single place that touches the MAF run/output API. Runs a workflow in-process to
/// completion within the current job segment (MAF never holds the durable wait, DL-018) and
/// returns the <see cref="RunState"/> yielded by the named terminal node. Fails loud if no
/// such output was produced rather than silently returning the input.
/// </summary>
internal static class MafWorkflowRunner
{
    public static async Task<RunState> RunToOutputAsync(
        Workflow workflow,
        RunState input,
        string terminalExecutorId,
        CancellationToken cancellationToken)
    {
        Run run = await InProcessExecution
            .RunAsync(workflow, input, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        RunState? output = null;
        foreach (WorkflowEvent evt in run.NewEvents)
        {
            if (evt is WorkflowOutputEvent outputEvent
                && outputEvent.ExecutorId == terminalExecutorId
                && outputEvent.Is<RunState>())
            {
                output = outputEvent.As<RunState>();
            }
        }

        return output ?? throw new InvalidOperationException(
            $"MAF workflow produced no RunState output from terminal node '{terminalExecutorId}'.");
    }
}
