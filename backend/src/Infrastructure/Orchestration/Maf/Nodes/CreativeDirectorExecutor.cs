using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Microsoft.Agents.AI.Workflows;

namespace Backend.Infrastructure.Orchestration.Maf.Nodes;

/// <summary>
/// Creative Director node (DL-019): owns <em>how it looks</em> — visual concept, style and
/// colour tokens, media-prompt brief. Consumes <see cref="ContentStrategy"/>, so it runs
/// after the Content Strategist. Deterministic stub: returns a canned
/// <see cref="CreativeDirection"/> and records a "creative" span. Writes only its slice.
/// </summary>
public sealed class CreativeDirectorExecutor : Executor<RunState, RunState>
{
    private readonly ITrace _trace;

    public CreativeDirectorExecutor(ITrace trace)
        : base("creative-director") => _trace = trace;

    public override ValueTask<RunState> HandleAsync(
        RunState message, IWorkflowContext context, CancellationToken cancellationToken = default)
        => new(RunAsync(message, cancellationToken));

    public async Task<RunState> RunAsync(RunState state, CancellationToken cancellationToken = default)
    {
        var creative = new CreativeDirection(
            VisualConcept: "stub-concept",
            StyleTokens: ["soft"],
            ColorTokens: ["#ffffff"],
            MediaPromptBrief: "stub-brief");

        var now = DateTimeOffset.UtcNow;
        var trace = await _trace.RecordAsync(
            state.Trace, state.RunId, state.BrandId, "creative", null, "ok", now, now, null, cancellationToken)
            .ConfigureAwait(false);

        return state with { Creative = creative, Trace = trace };
    }
}
