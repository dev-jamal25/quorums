using Backend.Core.Multitenancy;
using Backend.Core.Orchestration;
using Backend.Core.Secrets;
using Backend.Infrastructure.Configuration.Options;
using Backend.Infrastructure.Integrations.Meta;
using Backend.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace Backend.Infrastructure.Orchestration.Maf;

/// <summary>
/// The real Microsoft Agent Framework supervised graph behind the <see cref="IOrchestrator"/>
/// seam. Each segment builds and runs a MAF <see cref="Microsoft.Agents.AI.Workflows.Workflow"/>
/// of real agent nodes and returns the terminal <see cref="RunState"/>; the durable
/// checkpoint/exit/resume stays in the <c>AgentRun</c> state machine (the Hangfire jobs). The
/// publish node delegates to the brand-scoped <see cref="PublishCoordinator"/> (robust idempotency,
/// DL-039) and resolves the per-brand Meta token via <see cref="ISecretsProvider"/> (DL-011); these
/// scoped dependencies are resolved in the job's brand scope so the publish runs under RLS.
/// </summary>
public sealed class MafOrchestrator : IOrchestrator
{
    private readonly GenerationAgentDeps _gen;
    private readonly PublishCoordinator _coordinator;
    private readonly AppDbContext _db;
    private readonly IBrandScope _scope;
    private readonly ISecretsProvider _secrets;
    private readonly string _publicBaseUrl;

    public MafOrchestrator(
        GenerationAgentDeps gen,
        PublishCoordinator coordinator,
        AppDbContext db,
        IBrandScope scope,
        ISecretsProvider secrets,
        IOptions<StorageOptions> storageOptions)
    {
        _gen = gen;
        _coordinator = coordinator;
        _db = db;
        _scope = scope;
        _secrets = secrets;
        _publicBaseUrl = storageOptions.Value.PublicBaseUrl;
    }

    public Task<RunState> RunGenerationAsync(RunState state, CancellationToken cancellationToken = default)
        => MafWorkflowRunner.RunToOutputAsync(
            GenerationWorkflowFactory.Build(_gen),
            state,
            GenerationWorkflowFactory.TerminalId,
            cancellationToken);

    public Task<RunState> RunRegenerateAsync(
        RunState state, RegenerateMode mode, CancellationToken cancellationToken = default)
        => MafWorkflowRunner.RunToOutputAsync(
            RegenerateWorkflowFactory.Build(_gen, mode),
            state,
            RegenerateWorkflowFactory.TerminalId,
            cancellationToken);

    public Task<RunState> RunPublishAsync(RunState state, CancellationToken cancellationToken = default)
        => MafWorkflowRunner.RunToOutputAsync(
            PublishWorkflowFactory.Build(_coordinator, _db, _scope, _gen.Constraints, _secrets, _gen.Trace, _publicBaseUrl),
            state,
            PublishWorkflowFactory.TerminalId,
            cancellationToken);
}
