---
paths:
  - "backend/src/Core/Domain/**/*.cs"
  - "backend/src/Infrastructure/Persistence/**/*.cs"
---

# Persistence, Audit & RLS

<!-- Loads when Claude edits a brand-scoped entity (Core/Domain) or the EF persistence layer
     (AppDbContext + its OnModelCreating config, Persistence/Migrations). Entity configuration is
     inline in AppDbContext.OnModelCreating — there are no separate IEntityTypeConfiguration
     classes. The RLS-in-migration convention is owned by migrations.md and is NOT restated here. -->

## Audit is durable and append-only
- `ApprovalAction` (human gate actions) and `PublishRecord` (publish outcomes) are append-only business records. Never UPDATE a row; a correction is a new row. The gate is re-entrant, so a run may have multiple `ApprovalAction` rows.
- The human edit overlay (`EditedCaption` / `EditedHashtags`) lives on `ApprovalAction`, NEVER on `RunState.Draft`.

## Audit is RLS-scoped
- `ApprovalAction` and `PublishRecord` are brand-scoped, like every other brand-scoped table. The RLS policy convention itself — ENABLE + FORCE, shipped in the table's creating migration via `migrationBuilder.Sql(...)` — is owned by `migrations.md` ("RLS travels with the table"); runtime scoping by the `app.current_brand` GUC is owned by `infrastructure.md`. Do not restate it here.
- Audit-specific: the `Category=Isolation` two-brand test MUST cover audit reads (`ApprovalAction`, `PublishRecord`) — zero cross-brand leakage.

## Audit is independent of tracing
- Audit writes go straight to Postgres and are NEVER gated by Langfuse / `ITrace`. Tracing is optional observability (may fall back to local or be absent); the audit must always persist. Do not route an approval/publish record through the trace.

## Idempotency source of truth
- `PublishRecord` (keyed by `contentItemId`) is the source of truth for the pre-publish "already published?" guard. The publish path reads it before the publish step (see `orchestration.md`).

## Enum persistence
- `RunStatus` members are APPEND-only: `Scheduled` and `Cancelled` are new values; never renumber existing ones. If stored as int via `HasConversion`, the numeric values are load-bearing; if stored as a string, the member names are.
