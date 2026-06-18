using Backend.Core.Generation.PlatformConstraints;
using Backend.Core.Multitenancy;
using Backend.Core.Orchestration;
using Backend.Core.Secrets;
using Backend.Infrastructure.Integrations.Meta;
using Backend.Infrastructure.Orchestration.Maf.Nodes;
using Backend.Infrastructure.Persistence;
using Microsoft.Agents.AI.Workflows;

namespace Backend.Infrastructure.Orchestration.Maf;

/// <summary>
/// Builds the resume-segment graph: a single <c>publishing</c> node. A fresh
/// <c>ResumeRun</c> rehydrates <see cref="RunState"/> from the checkpoint and runs this graph;
/// MAF runs only intra-segment (DL-018), so one node is all the resume segment needs. The node
/// delegates to <see cref="PublishCoordinator"/> and resolves the brand token via
/// <see cref="ISecretsProvider"/>, both brand-scoped.
/// </summary>
internal static class PublishWorkflowFactory
{
    public const string TerminalId = "publishing";

    public static Workflow Build(
        PublishCoordinator coordinator,
        AppDbContext db,
        IBrandScope scope,
        PlatformConstraintSet constraints,
        ISecretsProvider secrets,
        ITrace trace)
    {
        var publishing = new PublishingExecutor(coordinator, db, scope, constraints, secrets, trace);

        return new WorkflowBuilder(publishing)
            .WithOutputFrom(publishing)
            .Build();
    }
}
