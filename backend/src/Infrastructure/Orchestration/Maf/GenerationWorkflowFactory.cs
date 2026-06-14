using Backend.Core.Orchestration;
using Backend.Core.Storage;
using Backend.Infrastructure.Orchestration.Maf.Nodes;
using Microsoft.Agents.AI.Workflows;

namespace Backend.Infrastructure.Orchestration.Maf;

/// <summary>
/// Builds the generation-segment graph: the fixed spine
/// <c>supervisor-entry → strategy → creative → (copywriting ∥ media) → assembly</c>.
/// Copywriting and Media Generation fork in parallel after the Creative Director and join at
/// the assembly node — the one genuine concurrency win (DL-017). The assembly node is an
/// <see cref="AggregatingExecutor{TInput, TAggregate}"/> driven by the barrier fan-in; its fold
/// is <see cref="AssemblyMerge.Fold"/>. The two designed-for stubs (Ads, Analytics) are bound
/// into the graph but left off the active spine (DL-019).
/// </summary>
internal static class GenerationWorkflowFactory
{
    public const string TerminalId = "assembly";

    public static Workflow Build(IStorageService storage, ITrace trace)
    {
        var entry = new SupervisorEntryExecutor();
        var strategy = new ContentStrategistExecutor(trace);
        var creative = new CreativeDirectorExecutor(trace);
        var copy = new CopywritingExecutor(trace);
        var media = new MediaGenerationExecutor(storage, trace);
        var assembly = new AggregatingExecutor<RunState, RunState>(TerminalId, AssemblyMerge.Fold);

        var builder = new WorkflowBuilder(entry)
            .AddEdge(entry, strategy)
            .AddEdge(strategy, creative)
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
