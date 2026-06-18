namespace Backend.Core.Domain;

/// <summary>
/// The kind of human action recorded on an <see cref="ApprovalAction"/> audit row (DL-035, DL-036,
/// DL-037, DL-040). Distinct from <see cref="ApprovalDecision"/>, which types the in-memory
/// <c>RunState.Approval</c> slice — this is the persisted audit taxonomy and covers the full gate
/// surface (edits, scheduling, cancel, regenerate). Persisted as text; members are append-only.
/// </summary>
public enum ApprovalActionType
{
    Approve,
    ApproveWithEdit,
    ApproveWithSchedule,
    Reject,
    Cancel,
    Regenerate,
}
