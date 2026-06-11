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
