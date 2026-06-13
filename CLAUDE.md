# CLAUDE.md

<!-- Autonomous Digital Marketing Agency — capstone. Refined from /init against the frozen Decision Logs.
     Source of truth: Product_Identity_and_Capstone_Scope.md, System_Architecture_Foundation.md,
     Agent_Orchestration_Design.md. This file directs behavior; those files hold the full rationale. -->

## Project Overview (The Why)

Claude-supervised multi-agent system that turns one DTC brand's brief into on-brand Instagram content (image + caption) through a deterministic agent graph. Publishing and paid actions are human-gated; Meta is mocked behind a swappable interface. Goal: **prove production-shaped AI-engineering ability, defensible line-by-line in review** — not ship a product.

**Frozen invariants — DO NOT re-decide. These are settled in the Decision Logs (DL-001…DL-023).**

- **Claude orchestrates; Gemini is a media *tool*** (`IMediaGenerationTool`), never an orchestrator (DL-001).
- **Multi-brand isolation is structural, from commit one.** Postgres RLS on every brand-scoped table; brand scope bound by an EF `DbConnectionInterceptor` running transaction-scoped `set_config('app.current_brand', …, true)`. A manual `WHERE brand_id` is **NEVER** the isolation mechanism (DL-002, DL-007).
- **Meta is mocked-first** behind `IMetaIntegration`; CI runs on mocks only. Live Meta is optional bonus behind the same interface (DL-004).
- **Human gate before any publish or paid action.** Never auto-publish (DL-005, DL-021).
- **Runs are durable jobs, not HTTP requests.** Hangfire on Postgres; the gate checkpoints `RunState`→`RunCheckpoint` and ends `ExecuteRun`; approval enqueues `ResumeRun`. State passes through Postgres, never through job payloads (DL-006).
- **Supervisor is the sole writer of `RunState.Phase`, `Draft`, `Budget`.** Agents write their declared slice only; handoffs are typed records, never free-form text (DL-020).
- **Secrets via Vault** (`ISecretsProvider`): KV→Options for app config; per-brand Meta tokens are Transit-encrypted ciphertext in the RLS-scoped `BrandMetaConnection`. Never inline a secret; never log a token or write a decrypt to disk (DL-011).
- **Side effects are idempotent:** MinIO keyed by `assetId`, publish keyed by `contentItemId` — a retried Hangfire segment must not duplicate (DL-022).
- **Embeddings = self-hosted nomic-embed-text-v1.5.** Prefix `search_document:` on corpus, `search_query:` on queries; pgvector column dim MUST equal model output dim (768 default); cosine distance, normalized vectors (DL-016).
- **Stack is .NET by employer mandate.** Bootcamp Python standards transfer as *principles*; idioms are .NET (DL-015).

## Tech Stack (The What)

- **Backend:** one `Backend.sln` — `Api` (ASP.NET Core, .NET 10 LTS), `Worker` (Worker Service + Hangfire), `Core` (domain + interfaces), `Infrastructure` (EF Core/Npgsql, Vault, MinIO, integrations, retrieval). `Api` and `Worker` ship from one publish output.
- **Frontend:** Next.js (React, TypeScript) in `frontend/`. No business logic; talks to the API via a typed `lib/api-client.ts`.
- **Data:** Postgres + pgvector — RLS isolation, embeddings, run checkpoints, **Hangfire job store** (own schema). EF Core Migrations own RLS SQL via `migrationBuilder.Sql`.
- **Infra:** Redis (`IDistributedCache`, not a queue broker), MinIO (`IStorageService`), Vault dev-mode (VaultSharp), Ollama embedding server. 8 services, one `docker-compose.yml`.
- **AI:** Claude (Anthropic API + .NET MCP SDK), Microsoft Agent Framework 1.0 (intra-segment graph only — the state machine owns the durable wait), Gemini (HttpClient behind interface). Langfuse tracing.
- **Validation:** DTOs + FluentValidation; `[ApiController]` auto-400 ProblemDetails. Errors surface as status codes, never `200` with an error body.

## Build / Test Commands (The How)

```bash
# backend
dotnet build Backend.sln -warnaserror        # nullable + analyzer warnings ARE errors
dotnet format --verify-no-changes            # style gate
dotnet test                                  # full xUnit suite (unit + Testcontainers integration)
dotnet test --filter Category=Isolation      # two-brand RLS leakage test
dotnet ef migrations add <Name> -p Infrastructure -s Api   # NEVER hand-edit an applied migration
docker compose up --build                    # full 8-service demo target

# frontend (cd frontend/)
npm run build        # must pass before done
npx tsc --noEmit     # type-check
npm run lint
```

## Code Verification Checklist — RUN these before declaring ANY task complete

State the verification method, then **actually run it and read the output.** No silent "hope it works." If a check is genuinely impossible, say so explicitly.

1. **Types:** `dotnet build Backend.sln -warnaserror` green (nullable refs + Roslyn analyzers clean). Frontend: `npx tsc --noEmit` clean.
2. **Format/lint:** `dotnet format --verify-no-changes` and `npm run lint` pass.
3. **Tests:** `dotnet test` green. For ANY change touching data access, **`dotnet test --filter Category=Isolation` MUST pass** — two seeded brands, zero leakage across query, storage path, and token decrypt.
4. **Durable resume (any orchestration/worker change):** kill the worker after the gate checkpoint, approve, confirm `ResumeRun` finishes the run with no data loss and no duplicate asset or double publish.
5. **Boundaries:** new external input has a DTO + FluentValidation; new tool call returns a structured `ToolError`, never an exception into the graph.
6. **Secrets hygiene:** `gitleaks` clean; no key, token, or connection string in code, logs, or committed config.
7. **Migrations:** schema change ships as a new EF migration with the RLS policy for any brand-scoped table; `docker compose up` applies cleanly from an empty volume.

**Done = every applicable box green.** A task that compiles but skips the isolation or resume check is **NOT** done.

## Scaffold Hardening — known-good patterns (feat/brand-onboarding, 2026-06-13)

Six defects fixed during onboarding; document them here so a fresh clone never replays them.

**Vault config binding** — use `configuration.GetValue<bool>("Vault:Enabled", false)` (tolerates missing key and empty string). `GetSection().Get<VaultOptions>()` throws on an invalid bool string. Exact fix: `Infrastructure/Configuration/VaultConfigurationExtensions.cs`.

**Host:port convention** — `*__Address` / `*__Endpoint` values store `host:port` ONLY (no `http://`). The application prepends `http://` at registration time. Both `VaultConfigurationExtensions` and `HealthCheckRegistration` prepend the scheme; the `.env` / `docker-compose.yml` environment blocks never include it.

**Vault health check must be feature-gated** — register the Vault URL-group check only when `configuration.GetValue<bool>("Vault:Enabled", false)` is true, so `Vault:Enabled=false` never causes `/health` to report Unhealthy. Exact fix: `Api/HealthChecks/HealthCheckRegistration.cs`.

**Vault compose service** — add `command: server -dev` and `SKIP_SETCAP: "true"` to suppress the CAP\_SETFCAP error on Docker Desktop / WSL2. `cap_add: [IPC_LOCK]` stays.

**Ollama init pattern** — pull the model via a one-shot `ollama-init` service (`profiles: ["embeddings"]`, `command: pull nomic-embed-text:v1.5`, `depends_on: embeddings: condition: service_healthy`). `api` and `worker` declare `ollama-init: condition: service_completed_successfully` with `required: false` so the wait is active only when the profile is on.

**Dockerfile ENTRYPOINT split** — `ENTRYPOINT ["dotnet"]` + `CMD ["Backend.Api.dll"]`. The worker compose service uses `command: ["Backend.Worker.dll"]`, which overrides only `CMD`. If `ENTRYPOINT` carries the assembly name, compose `command` appends instead of replacing and the worker silently runs the API binary — jobs stay Queued forever.

**EF migrations on a fresh volume** — `docker compose up` does NOT auto-migrate. After `docker compose down -v`, run:
```
dotnet ef database update -p src/Infrastructure -s src/Api --connection "Host=localhost;Port=5432;Database=quorums;Username=postgres;Password=postgres"
```
`AppDbContextDesignTimeFactory` uses a design-time placeholder connection; always supply `--connection` for the live DB.

**Hangfire schema race** — both Api and Worker call `AddHangfireJobStore`, which runs `CREATE SCHEMA "hangfire"` at startup. On a clean volume they race; the loser crashes with `duplicate key violates unique constraint "pg_namespace_nspname_index"`. Mitigation: add `restart: unless-stopped` to both services so the loser auto-recovers after the winner creates the schema. Long-term fix: run `PostgreSqlObjectsInstaller` only in the Worker.

**Smoke test evidence (2026-06-13, feat/brand-onboarding commit 5512e55)**
- `POST /brands` → 200, `brandId`
- `POST /runs` (X-Brand-Id header) → 202, `runId`
- Worker: `ExecuteRunJob` ran; `UPDATE agent_runs SET status`, `INSERT INTO run_checkpoints` logged
- `GET /runs/{id}` → `status=2` (AwaitingApproval)
- `POST /runs/{id}/approval {"decision":"approve"}` → `GET` → `status=3` (Publishing) → `status=4` (Done)
- Second run: reject path → `status=6` (Rejected), no `ResumeRun` enqueued, phase unchanged
- `/health` → Healthy (postgres, redis, minio, embeddings, self — Vault absent because disabled)

## Slice c2 — real I/O on the durable seam (2026-06-13)

The deterministic stub orchestrator (no LLM, no MAF) now does real I/O behind
swappable interfaces. Three seams, all CI-mockable:

- **`IStorageService` / `MinioStorage`** — media step writes a real 1×1 PNG to MinIO
  at `brands/{brandId}/assets/{assetId}.png`. Asset id is
  `DeterministicGuid.From(runId, "asset")`, so a Hangfire retry overwrites one key
  (no duplicate). `LocalStorage` (in-memory) double for durability tests; real MinIO
  via `Testcontainers.Minio` for `Category=Storage`.
- **`IMetaIntegration` / `MockMetaIntegration`** — `ResumeRun` publishes via the mock
  (selected by `Meta:Mode=mock`); `externalRef = mock://meta/{DeterministicGuid(runId,
  "meta")}`, deterministic so a retry re-uses the same ref. `LiveMetaIntegration` is a
  present-but-throwing seam selected by `Meta:Mode=live`.
- **`ITrace`** — a span per node + per tool call, threaded through `RunState.Trace`
  (`TraceRefs(TraceId, SpanIds, Spans)`) so one trace spans the ExecuteRun→ResumeRun
  seam. `LangfuseTrace` (typed HttpClient, best-effort) only when `Langfuse:BaseUrl`
  + both keys are set; otherwise `LocalTraceRecorder` (no network). **Langfuse is
  optional and config-gated exactly like Vault — its absence never fails a run**, and
  the Langfuse health check registers only when configured. Surfaced at
  `GET /runs/{id}/trace`, loaded under the RLS-bound scope.

Failures from storage/publish surface as a `ToolError` on `RunState.Errors`, never an
exception into the graph (DL-022).

**Smoke evidence (2026-06-13, commit d2b4c74)** — `docker compose` full stack:
- `POST /brands` → `brandId`; `POST /runs` (X-Brand-Id) → `runId`
- After `ExecuteRun`: MinIO `mc ls` shows `quorums-media/brands/{brandId}/assets/{assetId}.png` (70 B); `status=2`
- `approve` → `ResumeRun` → `status=4` (Done); `Publish.ExternalRef = mock://meta/{guid}`, `Status=published`
- `GET /runs/{id}/trace` → one `traceId`, 5 spans: strategy, creative, copywriting, `media:minio.put` (~94 ms real write), `publishing:meta.publish`
- Exactly **1** object under the brand prefix (idempotent write)
- `/health` → Healthy (postgres, redis, minio, embeddings, self — Vault + Langfuse absent because unconfigured)
