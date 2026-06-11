---
paths:
  - "backend/src/Infrastructure/Migrations/**/*.cs"
---

# EF Core Migrations

<!-- Loads when Claude touches migrations. High blast radius — these run against a shared schema. -->

## Forward-only
- NEVER edit a migration that is already committed or applied. Schema changes ship as a NEW migration.
- Generate with `dotnet ef migrations add <Name> -p Infrastructure -s Api`. Read the generated `Up`/`Down` before committing.

## RLS travels with the table
- EF does not infer Row-Level Security. Any migration that creates a brand-scoped table MUST, in the same migration, enable RLS and create the policy via `migrationBuilder.Sql(...)`. `Down` reverses it.
- A new brand-scoped table without an RLS policy is an isolation hole. Treat a missing policy as a failing review.

## Data & verification
- No backfills or seeds that bypass RLS — seed through a brand-scoped context.
- `docker compose up` must apply the full migration chain cleanly from an empty volume. Verify before declaring done.
