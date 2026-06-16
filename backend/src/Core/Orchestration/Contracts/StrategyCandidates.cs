namespace Backend.Core.Orchestration.Contracts;

/// <summary>
/// The Content Strategist's output envelope: N = 3 distinct candidate strategies (DL-027/028).
/// The Supervisor's single synthesis call selects one (<see cref="SelectionDecision"/>);
/// <c>RunState.Strategy</c> then holds the chosen candidate while all three persist in the trace.
/// </summary>
public sealed record StrategyCandidates(IReadOnlyList<ContentStrategy> Candidates);
