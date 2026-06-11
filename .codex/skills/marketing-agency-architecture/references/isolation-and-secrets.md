# Isolation and Secrets (DL-007, DL-002, DL-009, DL-011)

Governs the three-layer isolation model and the secrets architecture. Immutable
input. Cross-brand leakage is prevented by **infrastructure**, not by remembering
a `WHERE` clause.

## Three-layer isolation model

| Layer | Mechanism |
|-------|-----------|
| **Data** | Postgres **Row-Level Security**: every brand-scoped table carries `brand_id`; policy `USING (brand_id = current_setting('app.current_brand')::uuid)`. A request-scoped `IBrandContext` (set from auth) feeds an **EF Core `DbConnectionInterceptor`** that runs `SELECT set_config('app.current_brand', @brandId, true)` after the connection opens — **transaction-scoped (`true`)**, so it is connection-pool-safe and resets at commit. |
| **Storage** | MinIO keys are brand-prefixed: `brands/{brand_id}/assets/{asset_id}`. The storage service derives the prefix from `IBrandContext`, **never from caller input**. |
| **Credentials** | Per-brand Meta tokens are stored as **ciphertext** in the RLS-scoped `BrandMetaConnection` entity; Vault **Transit** owns the keys and performs decrypt-on-use with an audit trail. |

Embeddings live in pgvector **inside the same Postgres**, mapped via
`Pgvector.EntityFrameworkCore`, so RAG rows fall under the same RLS policies — one
isolation surface, no external vector DB.

**Defensibility:** "Cross-brand leakage is prevented by the database, the
object-store key scheme, and the crypto layer — not by remembering a WHERE clause.
The RLS policies are identical Postgres; only the EF interceptor that sets the
session variable is .NET-specific."

## RLS implementation (Data layer)

- RLS policies live in **EF Core migrations** (raw SQL via `migrationBuilder.Sql`).
  Isolation is **versioned schema**, not a manual step.
- The SQL pattern is in `assets/rls_policy_migration.sql.template`. For each
  brand-scoped table: `ALTER TABLE … ENABLE ROW LEVEL SECURITY;` then
  `CREATE POLICY … USING (brand_id = current_setting('app.current_brand')::uuid)`.
- Consider `FORCE ROW LEVEL SECURITY` so the table owner is also bound (the
  application's DB role must not bypass the policy).

### The EF interceptor (the only place brand scope is bound)

- Implement `IBrandContext` as a **request-scoped** service carrying the current
  `brand_id`, populated from auth at the start of the request (and set explicitly
  inside worker jobs from the `AgentRun.BrandId`).
- Implement a `DbConnectionInterceptor` whose `ConnectionOpened` /
  `ConnectionOpenedAsync` runs:
  `SELECT set_config('app.current_brand', @brandId, true)` — the `true` makes it
  **transaction-scoped** and pool-safe.
- Register the interceptor on the `DbContext` via `AddInterceptors`.
- **Critical pitfall:** using `false` (session-scoped) causes the setting to bleed
  across pooled connections → cross-brand leakage. It must be `true`.

### Worker-side scope

Inside `ExecuteRunJob` / `ResumeRunJob`, there is no HTTP request. Bind
`IBrandContext` from `AgentRun.BrandId` before any DbContext query so the same
interceptor sets the session variable correctly.

## Data-model rule

**Every domain entity except `Brand` carries `brand_id` and an RLS policy.** A
table without one must be justified in its migration. Hangfire's own tables are
infrastructure, isolated in their own schema, and hold no brand data. Full
ownership map in `data-model-and-api.md`.

## Mandatory leakage gate

A two-brand RLS leakage test must pass before any feature work proceeds past Day 2.
It seeds two brands and proves queries, storage paths, and token decrypts cannot
cross scopes. Scaffold: `scripts/RlsLeakageTests.cs`; runner:
`scripts/run-rls-leakage-test.sh`.

---

## Secrets architecture (DL-011) — two distinct Vault uses

Defend them as **distinct**. Client is **VaultSharp**.

### 1. App config secrets → Vault KV → the Options pattern

- What: Anthropic key, Gemini key, Postgres connection string, MinIO credentials,
  Redis URL. **Static, app-wide.**
- How: loaded into strongly-typed Options classes (`IOptions<T>`,
  `ValidateOnStart`, `ValidateDataAnnotations`) so required values **fail at
  startup** — the .NET equivalent of validated settings.
- Documentation: `appsettings.Example.json` / `.env.example` document every key
  (see `assets/appsettings.Example.json.template`). Dev fallback is
  `appsettings.json` + environment variables; **never commit real secrets.**

### 2. Per-brand Meta tokens → Vault Transit + Postgres ciphertext

Tokens are **tenant data, not app config**:

```
PostgreSQL: BrandMetaConnection (EF entity)
  brand_id (RLS) | token_ciphertext | token_type | expires_at | scopes | rotated_at

Vault Transit:
  - owns the encryption key (transit/keys/brand-tokens)
  - encrypt on store, decrypt only at the moment of a Meta call
  - every decrypt is audited
```

- Decrypt **only inside `IMetaIntegration` at call time**. Never cache the
  plaintext to disk or logs.
- Refresh/revoke lifecycle is **designed-for**: the columns and the
  `IMetaIntegration` methods exist; live refresh logic ships only with the live
  Meta path (advanced scope, DL-004).

### Honesty boundary (state in review before being asked)

Vault runs in **dev mode** for the demo. The claim is "the secrets *architecture*
is right — centralized authority, envelope encryption, audited decrypts." The
documented hardening path: persistent backend, auto-unseal, TLS, AppRole auth,
per-brand Transit policies.

### Success signal

No plaintext secret in repo, image, DB, or logs; a token decrypt appears in Vault's
audit output during a live-path call.
