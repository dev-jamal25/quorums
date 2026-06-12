namespace Backend.Core.Orchestration;

public interface IOrchestrator
{
    Task<RunState> RunGenerationAsync(RunState state, CancellationToken cancellationToken = default);

    Task<RunState> RunPublishAsync(RunState state, CancellationToken cancellationToken = default);
}
