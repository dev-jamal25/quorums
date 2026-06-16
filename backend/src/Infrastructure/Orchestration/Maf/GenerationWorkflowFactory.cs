using Backend.Core.Orchestration;
using Backend.Infrastructure.Orchestration.Maf.Nodes;
using Microsoft.Agents.AI.Workflows;

namespace Backend.Infrastructure.Orchestration.Maf;

/// <summary>
/// Builds the generation-segment graph: the fixed spine
/// <c>supervisor-entry → strategy → selection → creative → (copywriting ∥ media) → assembly</c>.
/// The Supervisor selection step is a sequential control-plane executor inserted between the
/// Strategist and the Creative Director (no new phase/checkpoint). Copywriting and Media Generation
/// fork in parallel after the Creative Director and join at the assembly node (DL-017); the assembly
/// node is an <see cref="AggregatingExecutor{TInput, TAggregate}"/> whose fold is
/// <see cref="AssemblyMerge.Fold"/> (the sole writer of Draft/Phase/Budget). The two designed-for
/// stubs (Ads, Analytics) are bound into the graph but left off the active spine (DL-019).
/// </summary>
internal static class GenerationWorkflowFactory
{
    public const string TerminalId = "assembly";

    public static Workflow Build(GenerationAgentDeps deps)
    {
        var entry = new SupervisorEntryExecutor();
        var strategy = new ContentStrategistExecutor(deps);
        var selection = new SupervisorSelectionExecutor(deps);
        var creative = new CreativeDirectorExecutor(deps);
        var copy = new CopywritingExecutor(deps);
        var media = new MediaGenerationExecutor(deps);
        var assembly = new AggregatingExecutor<RunState, RunState>(TerminalId, AssemblyMerge.Fold);

        var builder = new WorkflowBuilder(entry)
            .AddEdge(entry, strategy)
            .AddEdge(strategy, selection)
            .AddEdge(selection, creative)
            .AddFanOutEdge(creative, new ExecutorBinding[] { copy, media })
            .AddFanInBarrierEdge(new ExecutorBinding[] { copy, media }, assembly)
            .WithOutputFrom(assembly);

        // Designed-for stubs (DL-019): present in the graph wiring and constructible, but off
        // the active spine (no inbound edge), so they are never exercised — not invented, not cut.
        builder.BindExecutor(new AdsOptimizationExecutor());
        builder.BindExecutor(new AnalyticsExecutor());

        return builder.Build(validateOrphans: false);
    }
}
