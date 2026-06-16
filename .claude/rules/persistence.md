---
paths:
  - "backend/src/Infrastructure/Persistence/**/*.cs"
---

# Persistence, Audit & RLS

<!-- Loads when Claude touches the EF persistence layer (entities, configurations, migrations).
     SCOPE NOTE: adjust the path glob above to wherever EF entities/migrations actually live.
     If infrastructure.md already owns the RLS-in-migration convention, fold the RLS section
     below into it and drop this file. -->

## Audit is durable and append-only
- `ApprovalAction` (human gate actions) and `PublishRecord` (publish outcomes) are append-only business records. Never UPDATE a row; a correction is a new row. The gate is re-entrant, so a run may have multiple `ApprovalAction` rows.
- The human edit overlay (`EditedCaption` / `EditedHashtags`) lives on `ApprovalAction`, NEVER on `RunState.Draft`.

## Audit is RLS-scoped
- `ApprovalAction` and `PublishRecord` are brand-scoped. Ship the RLS policy in the SAME EF migration that creates each table (`migrationBuilder.Sql(...)`), scoped by `app.current_brand`, exactly like every other brand-scoped table (ENABLE + FORCE). The `Category=Isolation` two-brand test MUST cover audit reads — zero cross-brand leakage.

## Audit is independent of tracing
- Audit writes go straight to Postgres and are NEVER gated by Langfuse / `ITrace`. Tracing is optional observability (may fall back to local or be absent); the audit must always persist. Do not route an approval/publish record through the trace.

## Idempotency source of truth
- `PublishRecord` (keyed by `contentItemId`) is the source of truth for the pre-publish "already published?" guard. The publish path reads it before the publish step (see `orchestration.md`).

## Enum persistence
- `RunStatus` members are APPEND-only: `Scheduled` and `Cancelled` are new values; never renumber existing ones. If stored as int via `HasConversion`, the numeric values are load-bearing; if stored as a string, the member names are.
