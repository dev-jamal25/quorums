---
name: marketing-agency-architecture
description: Encodes the frozen Phase 1 architecture of the Autonomous Digital Marketing Agency capstone (.NET 10 / ASP.NET Core, eight-service Docker Compose, Postgres Row-Level-Security multi-brand isolation, Hangfire durable agent runs with checkpoint/resume, Vault KV plus Transit secrets, MinIO storage, pgvector RAG with self-hosted nomic-embed-text-v1.5, React/Next.js frontend) so an IDE coding agent implements it without re-deciding anything. Use when implementing this capstone: scaffolding the Backend.sln solution, writing EF Core models and RLS migrations, wiring the six boundary interfaces, building the queue plus worker run pipeline, adding brand-knowledge RAG, or following the vertical-slice build order. Trigger phrases include 'implement the capstone', 'scaffold the marketing agency', 'set up RLS brand isolation', 'wire the Hangfire worker', 'build the agent run pipeline', 'add the brand-knowledge RAG'.
metadata:
  author: dev-jamal25
  version: 1.0.0
  project: Autonomous Digital Marketing Agency (SE Factory AIE capstone)
  source-of-truth: System_Architecture_Foundation.md (DL-006 through DL-016)
---

# Marketing Agency Architecture (frozen Phase 1)

This skill is the implementation contract for the Autonomous Digital Marketing
Agency capstone. It encodes the **frozen** Phase 1 architecture so that an IDE
coding agent can build the system directly, with no architectural decisions left
to make at the keyboard.

## CRITICAL — this architecture is immutable input

The decisions here are **frozen**. Do not re-design, re-decide, optimize, or add
architecture. Specifically:

- Do not substitute libraries, services, or patterns for "better" ones.
- Do not collapse, merge, or skip any of the eight services.
- Do not replace Postgres RLS with application-layer `WHERE` filtering.
- Do not move secrets out of Vault, or store Meta tokens as anything but
  Transit-encrypted ciphertext in the RLS-scoped table.
- Do not change the stack from .NET, the frontend from React/Next.js, the queue
  from Hangfire-on-Postgres, the embedding model from nomic-embed-text-v1.5, or
  the orchestration brain from Claude.

If something needed for implementation is genuinely missing or ambiguous, **stop
and surface it as an open question** (see `references/open-questions.md`). Never
invent a decision to fill the gap.

Scope reductions (less depth in a layer) are allowed **only on the architect's
explicit instruction** — never as an automatic time-saving choice.

## How to use this skill

1. Read this file for the decision map and the build order.
2. Before implementing any area, open the matching reference file (links below)
   for the exact interfaces, SQL, schemas, and constraints.
3. Implement strictly in the vertical-slice order in
   `references/build-order.md`. The slice must run end-to-end every day from the
   end of Day 3 onward; never break it to add depth.
4. Phase 2 (agent orchestration internals) is **not frozen yet**. Build the Day-3
   stub agent now; do not invent the multi-agent graph — it arrives as Phase 2
   output (see `references/open-questions.md`).

## Architecture at a glance

- **Stack:** .NET 10 LTS / ASP.NET Core Web API + .NET Worker Service; React/Next.js
  (TypeScript) frontend. One `Backend.sln`, layered `Api` / `Worker` / `Core` /
  `Infrastructure`, built from one publish output (zero API↔worker skew).
- **Eight services, one `docker-compose.yml`:** `api`, `worker`, `frontend`,
  `postgres` (pgvector), `redis`, `minio`, `vault`, `embeddings` (local model
  server). Full service table in `references/stack-and-topology.md`.
- **Orchestration brain:** Claude (MCP-native). **Media tool:** Gemini, called
  behind `IMediaGenerationTool`. No model other than Claude orchestrates.
- **Demo target:** `docker compose up`; no public URL required. Mocked Meta
  boundary — the full loop runs with zero live Meta access.

```
Browser → frontend (Next.js) → api (ASP.NET Core) ──enqueue──▶ Hangfire (Postgres)
                                     │                                  │
                              EF Core (RLS)                         worker (.NET)
                                     ▼                                  │
   postgres + pgvector ◀── run state / checkpoints / embeddings ──┐    ├─▶ minio
        redis (IDistributedCache, not the queue broker)           │    ├─▶ vault (KV + Transit)
                                                                   │    ├─▶ Claude API
                                                                   │    ├─▶ Gemini API
                                                                   │    ├─▶ embeddings (nomic-v1.5)
                                                                   │    └─▶ IMetaIntegration (mock|live)
```

## Frozen decision map (DL-006 … DL-016)

| DL | Decision | Reference file |
|----|----------|----------------|
| DL-006 | Run execution = queue + worker + durable checkpoint/resume (Hangfire on Postgres; `ExecuteRun`/`ResumeRun`; AgentRun state machine; human gate) | `references/run-execution-model.md` |
| DL-007 | Isolation data layer = Postgres RLS, transaction-scoped `set_config` via EF `DbConnectionInterceptor` fed by request-scoped `IBrandContext` | `references/isolation-and-secrets.md` |
| DL-008 | Frontend = Streamlit — **SUPERSEDED by DL-014** | `references/decision-log.md` |
| DL-009 | Object storage = MinIO behind `IStorageService`, brand-prefixed keys | `references/boundary-interfaces.md` |
| DL-010 | RAG in MVP = brand-knowledge CMS on pgvector in the same Postgres (inherits RLS) | `references/rag-and-embeddings.md` |
| DL-011 | Secrets = Vault KV → Options for app config; Vault Transit envelope-encryption for per-brand Meta tokens | `references/isolation-and-secrets.md` |
| DL-012 | Queue = Arq — **SUPERSEDED by DL-015 (Hangfire on Postgres)** | `references/decision-log.md` |
| DL-013 | Build order = vertical slice first, depth in place | `references/build-order.md` |
| DL-014 | Frontend = React/Next.js (TypeScript), supersedes DL-008 | `references/decision-log.md` |
| DL-015 | Backend stack reversed to .NET (employer mandate); full Python→.NET mapping | `references/stack-and-topology.md` |
| DL-016 | Embeddings = self-hosted nomic-embed-text-v1.5 over HTTP via `IEmbeddingProvider`; 768-dim, cosine, task prefixes | `references/rag-and-embeddings.md` |

Full, verbatim Decision Log entries (with rationale, trade-offs, success signals,
skill-spec notes) are in **`references/decision-log.md`**. Read the entry before
implementing the area it governs.

## The six boundary interfaces (the swappable spine)

Every external or risky dependency sits behind a C# interface with a mock; CI
runs entirely on mocks. Implement each as specified in
`references/boundary-interfaces.md`.

| Interface | Implementations | Purpose |
|-----------|-----------------|---------|
| `IMetaIntegration` | `MockMetaIntegration`, `LiveMetaIntegration` (optional) | Demo never depends on Meta approval (DL-004) |
| `IMediaGenerationTool` | `GeminiMediaTool`, `MockMediaTool` | Gemini is a tool, not the orchestrator (DL-001) |
| `IStorageService` | `MinioStorage` (default), `LocalStorage` (tests) | Brand-prefixed asset storage, S3-portable (DL-009) |
| `IRetrievalService` | `PgVectorRetrieval` | Brand-scoped RAG grounding (DL-010) |
| `ISecretsProvider` | `VaultProvider`, `EnvProvider` (tests) | KV + Transit behind one seam (DL-011) |
| `IEmbeddingProvider` | `NomicEmbeddingProvider` (HTTP), mock (CI) | Self-hosted embeddings (DL-016) |

Register in `Program.cs` with explicit lifetimes: Singleton for clients/models,
Scoped for per-request, Transient otherwise. Constructor injection only.

## Three-layer isolation (non-negotiable, from commit one)

Cross-brand leakage is prevented by infrastructure, not by remembering a `WHERE`
clause. Full SQL and wiring in `references/isolation-and-secrets.md`.

- **Data:** Postgres RLS on every brand-scoped table; policy
  `USING (brand_id = current_setting('app.current_brand')::uuid)`. An EF Core
  `DbConnectionInterceptor` runs `SELECT set_config('app.current_brand', @brandId, true)`
  after the connection opens — **transaction-scoped (`true`)** so it is
  connection-pool-safe and resets at commit. Brand scope is bound from
  `IBrandContext` (set from auth), never from caller input.
- **Storage:** MinIO keys are `brands/{brand_id}/assets/{asset_id}`; the prefix is
  derived from `IBrandContext`.
- **Credentials:** per-brand Meta tokens stored as **ciphertext** in the
  RLS-scoped `BrandMetaConnection`; Vault **Transit** owns the key and
  decrypts-on-use with an audit trail.

RLS policies live in **EF Core migrations** (raw SQL via `migrationBuilder.Sql`),
so isolation is versioned schema, not a manual step. pgvector tables carry the
same policies — one isolation surface, no external vector DB.

**Mandatory gate:** a two-brand RLS leakage test must pass before any feature work
proceeds past Day 2. Scaffold provided in `scripts/RlsLeakageTests.cs`; runner in
`scripts/run-rls-leakage-test.sh`.

## Build order (implement in exactly this sequence)

Invariant: from the **end of Day 3** onward the demo runs end-to-end every day;
depth is added in place; the slice is never broken. Summary below; full per-day
detail in `references/build-order.md`.

1. **Day 1** — Solution scaffold (Api/Worker/Core/Infrastructure), compose with all
   eight services up, Options + Vault KV wired, `GET /health` green, CI skeleton
   (`dotnet build`/`test`, `dotnet format`, analyzers, gitleaks).
2. **Day 2** — EF model v1 + migration + **RLS policies + the EF interceptor**;
   prove with the two-brand leakage test; Brand onboarding endpoint.
3. **Day 3** — **Thin slice complete:** `POST /runs` → Hangfire `ExecuteRun` → one
   stub agent → mock media → MinIO write → checkpoint → approve in dashboard →
   `ResumeRun` → mock publish → trace visible.
4. **Day 4** — Real orchestration graph (Phase 2 output) replaces the stub;
   structured tool errors; run trace persisted.
5. **Day 5** — RAG: bring up the embedding server, knowledge CMS endpoints, ingest
   → chunk → embed (`search_document:`) → pgvector; queries embed with
   `search_query:`; Transit token flow for `BrandMetaConnection`.
6. **Day 6** — Generation quality pass; Gemini live behind the tool interface; cost
   ceilings.
7. **Day 7** — Eval suite; CI gates on real thresholds; golden sets.
8. **Day 8** — Demo script, README + architecture diagram, trace polish, buffer.

## Reference files

- `references/decision-log.md` — full DL-006…DL-016 entries (authoritative).
- `references/stack-and-topology.md` — eight services, the compose topology, the
  Python→.NET mapping table, .NET version note.
- `references/run-execution-model.md` — Hangfire jobs, AgentRun state machine,
  checkpoint/resume, human-approval gate, end-to-end data flow.
- `references/isolation-and-secrets.md` — three-layer isolation, RLS SQL, the EF
  interceptor, `IBrandContext`, Vault KV → Options, Transit envelope encryption.
- `references/boundary-interfaces.md` — all six interfaces, implementations, DI
  lifetimes, registration patterns.
- `references/rag-and-embeddings.md` — pgvector, nomic-embed-text-v1.5, task
  prefixes, 768-dim, cosine, normalization, ingest pipeline.
- `references/data-model-and-api.md` — data-model ownership map, entity list, RLS
  rule, the initial API surface.
- `references/repository-structure.md` — the monorepo layout.
- `references/build-order.md` — the full day-by-day vertical-slice schedule.
- `references/open-questions.md` — deferrals (Phase 2 orchestration) and the few
  documented ambiguities; consult before assuming anything not stated.

## Assets and scripts

- `scripts/RlsLeakageTests.cs` — two-brand RLS leakage test scaffold (xUnit +
  Testcontainers). Fill in the seeded brands; it must pass before feature work.
- `scripts/run-rls-leakage-test.sh` — runs the leakage test against a disposable
  Postgres.
- `assets/docker-compose.skeleton.yml` — the eight-service compose skeleton
  (service names, images, dependencies) derived from the architecture.
- `assets/rls_policy_migration.sql.template` — the RLS enable + policy SQL pattern
  to embed in an EF migration.
- `assets/appsettings.Example.json.template` — documents every config key (no real
  secrets), the Options-pattern source.

## Common pitfalls (fail the review if violated)

- **Session-scoped `set_config` instead of transaction-scoped** → pool bleed
  across brands. Must be `set_config(..., true)`.
- **Missing/mismatched embedding prefixes** (`search_document:` on ingest,
  `search_query:` on queries) → silently degraded retrieval.
- **pgvector column dim ≠ embedding output dim** (default 768) → migration/runtime
  mismatch. Set the column dim in the EF migration to match.
- **Redis used as the queue broker** → wrong. Redis is `IDistributedCache` only;
  the queue/job store is Hangfire-on-Postgres.
- **Long work in a controller** → controllers enqueue Hangfire jobs and return
  `202`; they never run the agent loop.
- **Decrypting a Meta token outside `IMetaIntegration` call-time, or caching it to
  disk/logs** → violates the secrets boundary.
- **A brand-scoped entity without an RLS policy** → every domain entity except
  `Brand` carries `brand_id` and a policy; a table without one must be justified
  in its migration.

## Example: implementing the RLS isolation layer (Day 2)

1. Open `references/isolation-and-secrets.md`.
2. Add `brand_id uuid` to every brand-scoped entity per
   `references/data-model-and-api.md`.
3. In a new EF migration, after `CreateTable`, call `migrationBuilder.Sql(...)`
   using the pattern in `assets/rls_policy_migration.sql.template` to
   `ENABLE ROW LEVEL SECURITY` and `CREATE POLICY` on each brand-scoped table.
4. Implement `IBrandContext` (request-scoped) and a `DbConnectionInterceptor` that
   runs `SELECT set_config('app.current_brand', @brandId, true)` on connection
   open; register the interceptor on the `DbContext`.
5. Run `scripts/run-rls-leakage-test.sh`. It seeds two brands and asserts queries,
   storage paths, and token decrypts cannot cross scopes. **It must pass before
   continuing.**

## Example: routing a run through the worker (Day 3)

1. Open `references/run-execution-model.md`.
2. `POST /runs` creates `AgentRun(status=queued)`, enqueues `ExecuteRun(runId)` on
   Hangfire, returns `202` + `run_id`. No agent logic in the controller.
3. The worker's Hangfire server runs `ExecuteRunJob`: stub agent → `MockMediaTool`
   → `MinioStorage` write under `brands/{brand_id}/…` → checkpoint state to
   Postgres → set `AgentRun → awaiting_approval` → job completes (nothing held in
   memory).
4. `POST /runs/{id}/approval` records the decision and enqueues `ResumeRun(runId)`.
5. `ResumeRunJob` resumes from the checkpoint → `MockMetaIntegration.PublishAsync`
   → `AgentRun → done`; trace viewable in the dashboard.
