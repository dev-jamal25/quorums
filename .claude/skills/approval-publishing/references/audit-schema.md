# Audit: ApprovalAction and persisted PublishResult (DL-040)

**Model A**: human actions on `ApprovalAction`, the system publish outcome on a persisted `PublishRecord` (one row per `(ContentItem, Channel)`, DL-055). The unified per-post timeline is a read projection, not a third table. `ApprovalAction` is strictly append-only; `PublishRecord` has a single in-flight→finalized update (see below). Both are **durable, RLS-scoped, and never gated by Langfuse** — disabling tracing must not drop the audit. (Angle brackets below are required C# generic syntax inside code blocks only.)

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
    Guid ContentItemId,                  // idempotency key — with Channel (DL-055)
    PublishChannel Channel,              // Instagram | FacebookPage (DL-055); one row per (ContentItem, Channel)
    string? CreationId,                  // Meta container id (IG) / unpublished photo id (Facebook), persisted BEFORE publish (DL-042); null until created
    PublishStatus Status,
    string? ExternalRef,                 // published media id; set at finalize
    int AttemptCount,
    DateTimeOffset OccurredAt,
    EngagementKeys? EngagementKeys       // the Analytics agent reads these later (DL-038, Phase 7)
);
```

- This row is the **source of truth** for the robust creation-id idempotency guard (see `meta-integration.md`), keyed `(ContentItemId, Channel)`: `CreationId` is persisted (committed) immediately after create and BEFORE publish, so a crash-and-retry re-publishes the same container/photo (Meta dedups) rather than creating a second post. `ExternalRef != null` marks the record finalized. A `CreationId` with a null `ExternalRef` is an in-flight publish to recover, not a finished one.
- **Mutability** — unlike `ApprovalAction` (strictly append-only), `PublishRecord` is inserted in-flight (`CreationId` set, `ExternalRef` null) and completed by a **single finalizing update** (`ExternalRef` + `Status`). That one update is the only permitted mutation; it completes the publish operation's durable state and is not an audit-history rewrite. No other mutation is allowed — a re-publish recovers the existing record, it does not rewrite it.

## RLS

- Both tables are brand-scoped: ship the RLS policy in the same EF migration that creates them (`migrationBuilder.Sql`), scoped by `app.current_brand`, like every brand-scoped table.
- The `Category=Isolation` two-brand test MUST cover audit reads — zero cross-brand leakage.

## Timeline projection

The "everything that happened to this post, in order" view is a query that unions `ApprovalAction` rows and `PublishRecord` rows for a run, ordered by `OccurredAt` (a post published to both channels has one `PublishRecord` per channel). Do not introduce a separate audit-event table.

## Langfuse independence

Audit writes go straight to Postgres, gated by nothing. Do NOT route them through `ITrace` / Langfuse. The trace (`LangfuseTrace` or `LocalTraceRecorder`) is optional observability; the audit is a durable business record (DL-040). A reviewer's approval, edit, reject, cancel, regenerate, and the publish outcome must all persist whether or not tracing is configured.
