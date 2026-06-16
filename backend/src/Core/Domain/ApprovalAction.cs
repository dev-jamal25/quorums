namespace Backend.Core.Domain;

/// <summary>
/// Durable, append-only audit row for one human gate visit (DL-005, DL-021, DL-035, DL-040). The
/// gate is re-entrant (regenerate, DL-036), so a run may have multiple rows — one per visit. A
/// correction is a NEW row; rows are never updated. The edit overlay (<see cref="EditedCaption"/> /
/// <see cref="EditedHashtags"/>) lives here, NEVER on <c>RunState.Draft</c> (DL-035). These writes
/// go straight to Postgres and are never gated by Langfuse/tracing (DL-040).
/// </summary>
public sealed class ApprovalAction : IBrandScoped
{
    public Guid Id { get; set; }

    public Guid BrandId { get; set; }

    public Guid AgentRunId { get; set; }

    public ApprovalActionType Action { get; set; }

    /// <summary>Fixed demo principal in MVP (no auth system); captured for the future team drop-in.</summary>
    public string Actor { get; set; } = default!;

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Caption overlay for <see cref="ApprovalActionType.ApproveWithEdit"/> (DL-035).</summary>
    public string? EditedCaption { get; set; }

    /// <summary>Hashtag overlay for <see cref="ApprovalActionType.ApproveWithEdit"/> (DL-035).</summary>
    public List<string>? EditedHashtags { get; set; }

    /// <summary>The slot for <see cref="ApprovalActionType.ApproveWithSchedule"/> (DL-037), UTC.</summary>
    public DateTimeOffset? ScheduledFor { get; set; }

    /// <summary>Free-text reason for <see cref="ApprovalActionType.Reject"/> / <see cref="ApprovalActionType.Regenerate"/>.</summary>
    public string? Reason { get; set; }

    /// <summary>Regenerate mode (<c>same-angle</c> / <c>reselect-angle</c>) for <see cref="ApprovalActionType.Regenerate"/> (DL-036).</summary>
    public string? RegenerateMode { get; set; }
}
