namespace Backend.Core.Domain;

/// <summary>
/// Lifecycle of an <see cref="AgentRun"/> (DL-006, DL-037). The supervisor is the sole
/// writer of this value; agents never advance it directly. Persisted as text via
/// <c>HasConversion&lt;string&gt;()</c>, so the member NAMES are the stored representation —
/// new members are APPENDED, never renumbered (the explicit values document the order and
/// guard against a future switch to int storage).
/// </summary>
public enum RunStatus
{
    Queued = 0,
    Running = 1,
    AwaitingApproval = 2,
    Publishing = 3,
    Done = 4,
    Failed = 5,
    Rejected = 6,
    Scheduled = 7,    // approved, waiting for its scheduled slot (DL-037)
    Cancelled = 8,    // a scheduled run pulled before it fired (DL-037)
}
