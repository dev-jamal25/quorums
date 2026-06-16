namespace Backend.Core.Orchestration;

public interface IOrchestrator
{
    Task<RunState> RunGenerationAsync(RunState state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-runs Creative Director → Media on the existing run (DL-036) — the Supervisor rewinds the
    /// phase and, for <see cref="RegenerateMode.ReselectAngle"/>, reselects a banked angle; the
    /// Strategist is NOT re-invoked. Produces a fresh Draft back at the human gate.
    /// </summary>
    Task<RunState> RunRegenerateAsync(RunState state, RegenerateMode mode, CancellationToken cancellationToken = default);

    Task<RunState> RunPublishAsync(RunState state, CancellationToken cancellationToken = default);
}
