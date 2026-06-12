namespace Backend.Core.Domain;

/// <summary>
/// Lifecycle of an <see cref="AgentRun"/> (DL-006). The supervisor is the sole
/// writer of this value; agents never advance it directly.
/// </summary>
public enum RunStatus
{
    Queued,
    Running,
    AwaitingApproval,
    Publishing,
    Done,
    Failed,
    Rejected,
}
