using Backend.Core.Integrations;
using Backend.Core.Orchestration;
using Backend.Core.Storage;

namespace Backend.Infrastructure.Orchestration.Maf;

/// <summary>
/// The real Microsoft Agent Framework supervised graph behind the <see cref="IOrchestrator"/>
/// seam, replacing the <c>StubOrchestrator</c> mechanism. Each segment builds and runs a MAF
/// <see cref="Microsoft.Agents.AI.Workflows.Workflow"/> of deterministic stub nodes and returns
/// the terminal <see cref="RunState"/>; the durable checkpoint/exit/resume stays in the
/// <c>AgentRun</c> state machine (the Hangfire jobs), so the proven c1/c2 seam is unchanged.
/// Constructor signature matches the old orchestrator so DI and tests are a drop-in swap.
/// </summary>
public sealed class MafOrchestrator : IOrchestrator
{
    private readonly IStorageService _storage;
    private readonly IMetaIntegration _meta;
    private readonly ITrace _trace;

    public MafOrchestrator(IStorageService storage, IMetaIntegration meta, ITrace trace)
    {
        _storage = storage;
        _meta = meta;
        _trace = trace;
    }

    public Task<RunState> RunGenerationAsync(RunState state, CancellationToken cancellationToken = default)
        => MafWorkflowRunner.RunToOutputAsync(
            GenerationWorkflowFactory.Build(_storage, _trace),
            state,
            GenerationWorkflowFactory.TerminalId,
            cancellationToken);

    public Task<RunState> RunPublishAsync(RunState state, CancellationToken cancellationToken = default)
        => MafWorkflowRunner.RunToOutputAsync(
            PublishWorkflowFactory.Build(_meta, _trace),
            state,
            PublishWorkflowFactory.TerminalId,
            cancellationToken);
}
