# Approval-review DTO and action contracts (DL-041)

The frontend carries no business logic; the server provides the review payload and the list of currently-legal actions. Types below are illustrative C# records — adapt names to the codebase conventions. (C# generic syntax with angle brackets appears only inside the code blocks below; it is required .NET syntax.)

## GET /runs/{id}/review — the review payload

Returned to the approval dashboard so the reviewer decides with full context.

```csharp
public record RunReviewDto(
    Guid RunId,
    RunStatus Status,
    PostSurface Surface,                 // InstagramFeed | InstagramReel | InstagramStory
    string? ImageUrl,                    // brand-scoped served/presigned MinIO URL; null when BudgetDegraded
    string Caption,
    IReadOnlyList<string> Hashtags,
    GroundingDto Grounding,              // provenance the caption is based on (DL-028)
    bool BudgetDegraded,                 // true => caption-only, no image (DL-029)
    string? BudgetDegradedReason,
    SelectedAngleDto SelectedAngle,      // the chosen ContentStrategy (DL-027)
    IReadOnlyList<AngleSummaryDto> AlternativeAngles,  // the other banked N=3 (drives reselect-angle)
    DateTimeOffset? ScheduledFor,        // set when Status == Scheduled
    string? TraceUrl,                    // only when Langfuse configured
    IReadOnlyList<GateAction> AvailableActions  // server-computed; client never recomputes
);

public record GroundingDto(bool Grounded, IReadOnlyList<Guid> ChunkIdsUsed, double Confidence);
public record SelectedAngleDto(int ChosenIndex, string Pillar, string Objective, string Rationale);
public record AngleSummaryDto(int Index, string Pillar, string Objective);

public enum GateAction { Approve, Reject, Regenerate, Cancel }
```

### AvailableActions rules (server-computed)

- `Approve`, `Reject` — present when `Status == AwaitingApproval`.
- `Regenerate` — present when `Status == AwaitingApproval` AND the per-run regenerate count is below the DL-029 ceiling; omitted once the ceiling is reached.
- `Cancel` — present only when `Status == Scheduled`.

The reviewer must be able to *see* `Grounding`, `BudgetDegraded`, and `AlternativeAngles` so they approve with context and can drive `reselect-angle`. Do not omit them.

## POST /runs/{id}/approval — gate decision (decision-discriminated)

```csharp
public record ApprovalRequest(
    GateDecision Decision,               // Approve | Reject | Regenerate
    ApprovalEdits? Edits,                // Approve only; null = publish as-is
    DateTimeOffset? ScheduledFor,        // Approve only; null = publish immediately
    string? Reason,                      // Reject or Regenerate
    RegenerateMode? Mode                 // Regenerate only
);

public record ApprovalEdits(string? Caption, IReadOnlyList<string>? Hashtags);

public enum GateDecision { Approve, Reject, Regenerate }
public enum RegenerateMode { SameAngle, ReselectAngle }
```

### Validation (FluentValidation, fail-fast 400)

- `Decision == Approve` with `Edits`: `Edits.Caption` length at most 2200; `Edits.Hashtags` count at most 30 — the same `PlatformConstraints` check applied at publish (DL-030).
- `ScheduledFor`, when present: strictly in the future (UTC).
- `Decision == Regenerate`: `Mode` required; reject the request if the regenerate ceiling is already reached (defense-in-depth with the available-actions list).
- `Mode == ReselectAngle`: a valid alternative angle must exist.

### Effects

- `Approve` and no `ScheduledFor` → `Enqueue(ResumeRun)`, `Status` becomes `Publishing`.
- `Approve` with `ScheduledFor` → `Schedule(ResumeRun, ...)`, `Status` becomes `Scheduled`.
- `Reject` → `Status` becomes `Rejected` (terminal), no resume.
- `Regenerate` → Supervisor rewinds `Phase`, `Status` becomes `Running` (re-entrant gate).
- Every decision appends an `ApprovalAction` row (see `audit-schema.md`).

## POST /runs/{id}/cancel — cancel a scheduled run

```csharp
public record CancelRequest(string? Reason);
```

- Valid only when `Status == Scheduled`; otherwise return 409 Conflict.
- Deletes the Hangfire delayed job (`BackgroundJob.Delete`), `Status` becomes `Cancelled` (terminal), no publish.
- Appends an `ApprovalAction` row with `Action = Cancel`.
