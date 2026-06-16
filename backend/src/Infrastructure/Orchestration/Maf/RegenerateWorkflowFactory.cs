using Backend.Core.Orchestration;
using Backend.Infrastructure.Orchestration.Maf.Nodes;
using Microsoft.Agents.AI.Workflows;

namespace Backend.Infrastructure.Orchestration.Maf;

/// <summary>
/// Builds the regenerate-segment graph (DL-036): <c>supervisor-rewind → creative →
/// (copywriting ∥ media) → assembly</c>. It re-enters at the Creative Director — the Strategist and
/// the Supervisor selection are NOT re-run (the rewind node reselects the angle over the already-banked
/// DL-027 candidates when needed). Reuses the same CD/Copywriting/Media executors + the
/// <see cref="AssemblyMerge.Fold"/> aggregator as the generation graph, so the merge logic stays in
/// one place; the assembly node moves the run back to the human gate.
/// </summary>
internal static class RegenerateWorkflowFactory
{
    public const string TerminalId = "assembly";

    public static Workflow Build(GenerationAgentDeps deps, RegenerateMode mode)
    {
        var rewind = new SupervisorRewindExecutor(mode);
        var creative = new CreativeDirectorExecutor(deps);
        var copy = new CopywritingExecutor(deps);
        var media = new MediaGenerationExecutor(deps);
        var assembly = new AggregatingExecutor<RunState, RunState>(TerminalId, AssemblyMerge.Fold);

        return new WorkflowBuilder(rewind)
            .AddEdge(rewind, creative)
            .AddFanOutEdge(creative, new ExecutorBinding[] { copy, media })
            .AddFanInBarrierEdge(new ExecutorBinding[] { copy, media }, assembly)
            .WithOutputFrom(assembly)
            .Build();
    }
}
