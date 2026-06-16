# Audit: ApprovalAction and persisted PublishResult (DL-040)

**Model A**: human actions on `ApprovalAction`, the system publish outcome on a persisted `PublishRecord`. The unified per-post timeline is a read projection, not a third table. Both are **durable, RLS-scoped, and never gated by Langfuse** — disabling tracing must not drop the audit. (Angle brackets below are required C# generic syntax inside code blocks only.)

## ApprovalAction (human-action record, append-only)

One row per gate visit. The gate is re-entrant (regenerate, DL-036), so a run can have multiple rows.

```csharp
public record ApprovalAction(
    Guid Id,
    Guid RunId,
    Guid BrandId,                        // RLS-scoped
    ApprovalActionType Action,           // Approve | ApproveWithEdit | ApproveWithSchedule | Reject | Cancel | Regenerate
    string Actor,                        // fixed demo principal in MVP (no auth system)
    DateTimeOffset OccurredAt,
    string? EditedCaption,               // ApproveWithEdit (DL-035)
    IReadOnlyList<string>? EditedHashtags,
    DateTimeOffset? ScheduledFor,        // ApproveWithSchedule (DL-037)
    string? Reason,                      // Reject / Regenerate
    string? RegenerateMode               // Regenerate (DL-036)
);

public enum ApprovalActionType
{
    Approve, ApproveWithEdit, ApproveWithSchedule, Reject, Cancel, Regenerate
}
```

- `EditedCaption` / `EditedHashtags` are the **overlay** the Publishing node applies on resume. `RunState.Draft` is NOT modified (DL-035).
- Append-only: never update a row; a correction is a new row.

## PublishRecord (persisted system publish-outcome)

```csharp
public record PublishRecord(
    Guid Id,
    Guid RunId,
    Guid BrandId,                        // RLS-scoped
    Guid ContentItemId,                  // idempotency key (the guard reads this)
    PublishStatus Status,
    string? ExternalRef,                 // published media id
    int AttemptCount,
    DateTimeOffset OccurredAt,
    EngagementKeys? EngagementKeys       // the Analytics agent reads these later (DL-038, Phase 7)
);
```

- This row is the **source of truth** for the pre-publish idempotency guard ("already published?").

## RLS

- Both tables are brand-scoped: ship the RLS policy in the same EF migration that creates them (`migrationBuilder.Sql`), scoped by `app.current_brand`, like every brand-scoped table.
- The `Category=Isolation` two-brand test MUST cover audit reads — zero cross-brand leakage.

## Timeline projection

The "everything that happened to this post, in order" view is a query that unions `ApprovalAction` rows and `PublishRecord` rows for a run, ordered by `OccurredAt`. Do not introduce a separate audit-event table.

## Langfuse independence

Audit writes go straight to Postgres, gated by nothing. Do NOT route them through `ITrace` / Langfuse. The trace (`LangfuseTrace` or `LocalTraceRecorder`) is optional observability; the audit is a durable business record (DL-040). A reviewer's approval, edit, reject, cancel, regenerate, and the publish outcome must all persist whether or not tracing is configured.
