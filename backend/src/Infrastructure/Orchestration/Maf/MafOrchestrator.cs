using Backend.Core.Integrations;
using Backend.Core.Orchestration;

namespace Backend.Infrastructure.Orchestration.Maf;

/// <summary>
/// The real Microsoft Agent Framework supervised graph behind the <see cref="IOrchestrator"/>
/// seam. Each segment builds and runs a MAF <see cref="Microsoft.Agents.AI.Workflows.Workflow"/>
/// of real agent nodes (Claude via <c>IStructuredGenerator → IChatClient</c>, Gemini via
/// <c>IMediaGenerationTool</c>) and returns the terminal <see cref="RunState"/>; the durable
/// checkpoint/exit/resume stays in the <c>AgentRun</c> state machine (the Hangfire jobs). The
/// generation agents share one <see cref="GenerationAgentDeps"/> bundle (resolved in the job's
/// brand scope, so retrieval is RLS-bound); publishing keeps its mock/live Meta seam.
/// </summary>
public sealed class MafOrchestrator : IOrchestrator
{
    private readonly GenerationAgentDeps _gen;
    private readonly IMetaIntegration _meta;

    public MafOrchestrator(GenerationAgentDeps gen, IMetaIntegration meta)
    {
        _gen = gen;
        _meta = meta;
    }

    public Task<RunState> RunGenerationAsync(RunState state, CancellationToken cancellationToken = default)
        => MafWorkflowRunner.RunToOutputAsync(
            GenerationWorkflowFactory.Build(_gen),
            state,
            GenerationWorkflowFactory.TerminalId,
            cancellationToken);

    public Task<RunState> RunPublishAsync(RunState state, CancellationToken cancellationToken = default)
        => MafWorkflowRunner.RunToOutputAsync(
            PublishWorkflowFactory.Build(_meta, _gen.Trace),
            state,
            PublishWorkflowFactory.TerminalId,
            cancellationToken);
}
