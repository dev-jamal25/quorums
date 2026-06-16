# CLAUDE.md

<!-- Maintainer note (stripped from Claude's context): Autonomous Digital Marketing Agency capstone.
     Refined from /init against the frozen Decision Logs DL-001…DL-026. This file directs behavior;
     full rationale lives in Product_Identity_and_Capstone_Scope.md, System_Architecture_Foundation.md,
     Agent_Orchestration_Design.md. Per-subsystem detail lives in the skills, not here. -->

## Project Overview (The Why)

Claude-supervised multi-agent system that turns one DTC brand's brief into on-brand Instagram content (image + caption) through a deterministic agent graph. Publishing and paid actions are human-gated; Meta is mocked behind a swappable interface. Goal: **prove production-shaped AI-engineering ability, defensible line-by-line in review** — not ship a product.

### Frozen invariants — DO NOT re-decide (Decision Logs DL-001…DL-026)

- **Claude orchestrates; Gemini is a media *tool*** (`IMediaGenerationTool`), never an orchestrator (DL-001).
- **Multi-brand isolation is structural.** Postgres RLS on every brand-scoped table; brand scope bound by an EF `DbConnectionInterceptor` running transaction-scoped `set_config('app.current_brand', …, true)`. A manual `WHERE brand_id` is **NEVER** the isolation mechanism (DL-002, DL-007).
- **Meta is mocked-first** behind `IMetaIntegration`; CI runs on mocks only. Live Meta is optional bonus, same interface (DL-004).
- **Human gate before any publish or paid action.** Never auto-publish (DL-005, DL-021).
- **Runs are durable jobs, not HTTP requests.** Hangfire on Postgres; the gate checkpoints `RunState`→`RunCheckpoint` and ends `ExecuteRun`; approval enqueues `ResumeRun`. State passes through Postgres, never through job payloads (DL-006).
- **Supervisor is the sole writer of `RunState.Phase`, `Draft`, `Budget`.** Agents write their declared slice only; handoffs are typed records, never free-form text (DL-020).
- **Secrets via Vault** (`ISecretsProvider`): KV→Options for app config; per-brand Meta tokens are Transit-encrypted ciphertext in the RLS-scoped `BrandMetaConnection`. Never inline a secret; never log a token or write a decrypt to disk (DL-011).
- **Side effects are idempotent:** MinIO keyed by `assetId`, publish keyed by `contentItemId` — a retried Hangfire segment must not duplicate (DL-022).
- **Embeddings = self-hosted nomic-embed-text-v1.5.** Prefix `search_document:` on corpus, `search_query:` on queries; pgvector column dim MUST equal model output dim (768); cosine distance, normalized vectors (DL-016). Served via HF TEI, two containers (DL-024); cross-encoder rerank + sparse Postgres FTS hybrid retrieval (DL-025).
- **Stack is .NET by employer mandate.** Bootcamp Python standards transfer as *principles*; idioms are .NET (DL-015).

## Tech Stack (The What)

- **Backend:** one `backend/Backend.sln` — `Api` (ASP.NET Core, .NET 10 LTS), `Worker` (Worker Service + Hangfire), `Core` (domain + interfaces), `Infrastructure` (EF Core/Npgsql, Vault, MinIO, integrations, retrieval). Projects under `backend/src/*`; `Api` and `Worker` ship from one publish output.
- **Frontend:** Next.js (React, TypeScript) in `frontend/`. No business logic; talks to the API via a typed `lib/api-client.ts`.
- **Data:** Postgres + pgvector — RLS isolation, embeddings, run checkpoints, **Hangfire job store** (own schema). EF Core Migrations own RLS SQL via `migrationBuilder.Sql`.
- **Infra:** Redis (`IDistributedCache`, not a queue broker), MinIO (`IStorageService`), Vault dev-mode (VaultSharp), HF TEI (2 containers: nomic-embed + bge-reranker-v2-m3). 9 services, one `docker-compose.yml`.
- **AI:** Claude (Anthropic API + .NET MCP SDK), Microsoft Agent Framework 1.0 (intra-segment graph only — the state machine owns the durable wait), Gemini (HttpClient behind interface). Langfuse tracing.
- **Validation:** DTOs + FluentValidation; `[ApiController]` auto-400 ProblemDetails. Errors surface as status codes, never `200` with an error body.
- **Per-subsystem detail lives in the skills**, not here: `marketing-agency-architecture`, `agent-orchestration-graph`, `generation-pipeline`, `brand-knowledge-rag`, `dotnet-engineering-standards`.

### Generation pipeline (real agents, not stubs — DL-027…DL-030, see `generation-pipeline` skill)

- **MAF executor nodes** (`Infrastructure/Orchestration/Maf/Nodes/`): Supervisor entry → **Content Strategist (N=3 angles)** → **Supervisor selection** → Creative Director → Copywriting → Media Generation —[human gate]→ Publishing. Ads Optimization + Analytics are stubs.
- **Each agent emits one typed schema via a forced tool** (`ForcedToolGenerator`, `ChatToolMode.RequireSpecific`): `ContentStrategy`, `SelectionDecision`, `CreativeDirection`, `MediaPromptBrief`, `Caption` (+`Grounding`) in `Core/Orchestration/Contracts/`. Schema is generated from the record (DL-028); post-tool field validators enforce `PlatformConstraints` (caption/hashtag/aspect-ratio), selection-index range, and grounding honesty.
- **Per-run cost model** (`Core/Generation/Cost/`): `TokenBudget` + `MediaBudget` with a pre-Media gate and a global ceiling; over-ceiling **degrades** rather than overspends.
- **Offline/CI:** `DeterministicGenerationChatClient` + `DeterministicMediaGenerationTool`; live media is `LiveGeminiMediaTool` behind `IMediaGenerationTool` (DL-001).

### Run state machine + durable seam (config-gated, all CI-mockable)

- **`RunStatus`:** `Queued(0) → Running(1) → AwaitingApproval(2)` —[approve]→ `Publishing(3) → Done(4)`; `Failed(5)`; `Rejected(6)` (reject path enqueues no `ResumeRun`, phase unchanged).
- **`IStorageService`** — media writes `brands/{brandId}/assets/{assetId}.png`, `assetId = DeterministicGuid.From(runId,"asset")`; a retry overwrites one key (no duplicate). `LocalStorage` for durability tests; real MinIO via `Testcontainers.Minio` (`Category=Storage`).
- **`IMetaIntegration`** — `ResumeRun` publishes via mock (`Meta:Mode=mock`); `externalRef = mock://meta/{DeterministicGuid(runId,"meta")}`, stable across retry. `LiveMetaIntegration` is a present-but-throwing seam (`Meta:Mode=live`).
- **`ITrace`** — span per node + per tool call, threaded through `RunState.Trace`, spanning the `ExecuteRun→ResumeRun` seam. `LangfuseTrace` only when `Langfuse:BaseUrl` + both keys set, else `LocalTraceRecorder` (no network). **Like Vault, Langfuse is optional and config-gated — its absence never fails a run**; its health check registers only when configured. Surfaced at `GET /runs/{id}/trace` under the RLS-bound scope.
- Storage/publish failures surface as a `ToolError` on `RunState.Errors`, never an exception into the graph (DL-022).

## Build / Test Commands (The How)

Backend commands run from `backend/`; frontend from `frontend/`.

```bash
# backend (cd backend/)
dotnet build Backend.sln -warnaserror        # nullable + analyzer warnings ARE errors
dotnet format --verify-no-changes            # style gate
dotnet test                                  # full xUnit suite (unit + Testcontainers integration)
dotnet test --filter Category=Isolation      # two-brand RLS leakage test
dotnet ef migrations add <Name> -p src/Infrastructure -s src/Api   # NEVER hand-edit an applied migration
docker compose up --build                    # full 9-service demo target

# frontend (cd frontend/)
npm run build        # must pass before done
npx tsc --noEmit     # type-check
npm run lint
```

**EF migrations on a fresh volume** — `docker compose up` does NOT auto-migrate. After `docker compose down -v`, from `backend/`:

```bash
dotnet ef database update -p src/Infrastructure -s src/Api \
  --connection "Host=localhost;Port=5432;Database=quorums;Username=postgres;Password=postgres"
```

`AppDbContextDesignTimeFactory` uses a design-time placeholder connection; always pass `--connection` for the live DB.

### Known-good patterns — never replay these defects (feat/brand-onboarding)

- **Vault config binding:** use `configuration.GetValue<bool>("Vault:Enabled", false)` (tolerates missing key + empty string). `GetSection().Get<VaultOptions>()` throws on an invalid bool. Fix in `VaultConfigurationExtensions.cs`.
- **Host:port convention:** `*__Address` / `*__Endpoint` store `host:port` ONLY; the app prepends `http://` at registration (`VaultConfigurationExtensions`, `HealthCheckRegistration`). `.env` / compose never include the scheme.
- **Feature-gate the Vault health check:** register it only when `Vault:Enabled` is true, so `false` never makes `/health` Unhealthy (`HealthCheckRegistration.cs`).
- **Vault compose service:** `command: server -dev` + `SKIP_SETCAP: "true"` (suppresses CAP_SETFCAP on Docker Desktop/WSL2); keep `cap_add: [IPC_LOCK]`.
- **TEI two-container (DL-024):** `tei-embed` + `tei-rerank` on `ghcr.io/huggingface/text-embeddings-inference:cpu-1.6`, each with a named model-cache volume; `api`/`worker` gate on `condition: service_healthy` with `start_period: 120s` for first-run weight download. Ollama removed.
- **Dockerfile ENTRYPOINT split:** `ENTRYPOINT ["dotnet"]` + `CMD ["Backend.Api.dll"]`; worker overrides via `command: ["Backend.Worker.dll"]`. If `ENTRYPOINT` carries the assembly name, compose `command` appends instead of replacing — the worker silently runs the API and jobs stay Queued forever.
- **Hangfire schema race:** both Api and Worker run `CREATE SCHEMA "hangfire"` and race on a clean volume (`duplicate key … pg_namespace_nspname_index`). `restart: unless-stopped` on both auto-recovers; long-term, run `PostgreSqlObjectsInstaller` only in the Worker.

## Code Verification Checklist — RUN before declaring ANY task complete

State the verification method, then **actually run it and read the output.** No silent "hope it works." If a check is genuinely impossible, say so explicitly. **Done = every applicable box green** — a task that compiles but skips the isolation or resume check is **NOT** done.

1. **Types:** `dotnet build Backend.sln -warnaserror` green (nullable refs + Roslyn analyzers clean). Frontend: `npx tsc --noEmit` clean.
2. **Format/lint:** `dotnet format --verify-no-changes` and `npm run lint` pass.
3. **Tests:** `dotnet test` green. ANY change touching data access → **`dotnet test --filter Category=Isolation` MUST pass** (two seeded brands; zero leakage across query, storage path, token decrypt).
4. **Durable resume (any orchestration/worker change):** kill the worker after the gate checkpoint, approve, confirm `ResumeRun` finishes the run with no data loss, no duplicate asset, no double publish.
5. **Boundaries:** new external input has a DTO + FluentValidation; new tool call returns a structured `ToolError`, never an exception into the graph.
6. **Secrets hygiene:** `gitleaks` clean; no key, token, or connection string in code, logs, or committed config.
7. **Migrations:** schema change ships as a new EF migration with the RLS policy for every brand-scoped table; `docker compose up` applies cleanly from an empty volume.
