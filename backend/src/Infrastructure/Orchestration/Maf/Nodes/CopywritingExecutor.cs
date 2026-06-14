using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Microsoft.Agents.AI.Workflows;

namespace Backend.Infrastructure.Orchestration.Maf.Nodes;

/// <summary>
/// Copywriting node (DL-019): owns the caption — hook, body, hashtags. Forks in parallel
/// with Media Generation after the Creative Director. Deterministic stub: returns a canned
/// <see cref="Caption"/> and records a "copywriting" span. Writes only its slice; the
/// assembly node merges this branch with the media branch (DL-020).
/// </summary>
public sealed class CopywritingExecutor : Executor<RunState, RunState>
{
    private readonly ITrace _trace;

    public CopywritingExecutor(ITrace trace)
        : base("copywriting") => _trace = trace;

    public override ValueTask<RunState> HandleAsync(
        RunState message, IWorkflowContext context, CancellationToken cancellationToken = default)
        => new(RunAsync(message, cancellationToken));

    public async Task<RunState> RunAsync(RunState state, CancellationToken cancellationToken = default)
    {
        var caption = new Caption(
            Hook: "stub-hook",
            Body: "stub-body",
            Hashtags: ["#stub"]);

        var now = DateTimeOffset.UtcNow;
        var trace = await _trace.RecordAsync(
            state.Trace, state.RunId, state.BrandId, "copywriting", null, "ok", now, now, null, cancellationToken)
            .ConfigureAwait(false);

        return state with { Caption = caption, Trace = trace };
    }
}
