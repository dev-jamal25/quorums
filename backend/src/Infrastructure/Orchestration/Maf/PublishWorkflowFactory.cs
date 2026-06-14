using Backend.Core.Integrations;
using Backend.Core.Orchestration;
using Backend.Infrastructure.Orchestration.Maf.Nodes;
using Microsoft.Agents.AI.Workflows;

namespace Backend.Infrastructure.Orchestration.Maf;

/// <summary>
/// Builds the resume-segment graph: a single <c>publishing</c> node. A fresh
/// <c>ResumeRun</c> rehydrates <see cref="RunState"/> from the checkpoint and runs this graph;
/// MAF runs only intra-segment (DL-018), so one node is all the resume segment needs.
/// </summary>
internal static class PublishWorkflowFactory
{
    public const string TerminalId = "publishing";

    public static Workflow Build(IMetaIntegration meta, ITrace trace)
    {
        var publishing = new PublishingExecutor(meta, trace);

        return new WorkflowBuilder(publishing)
            .WithOutputFrom(publishing)
            .Build();
    }
}
