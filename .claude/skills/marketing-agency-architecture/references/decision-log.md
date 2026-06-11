# Decision Log — DL-006 … DL-016 (authoritative)

> Frozen extract from `System_Architecture_Foundation.md` and
> `Product_Identity_and_Capstone_Scope.md`. These entries are **immutable input**.
> Encode them; do not re-decide. Where an entry is superseded, the superseding
> entry governs and the old one is retained only as history.

Stack-level resolution recorded in the architecture doc:
**Backend reversed to .NET by employer mandate (DL-015), which supersedes the
stack-specific parts of DL-006, DL-007, DL-009, DL-011, and DL-012.** The body of
each entry below reflects .NET as current truth.

---

## DL-006 — Run execution model: queue + worker + durable checkpoint

> Partially superseded by DL-015: Arq → **Hangfire (Postgres storage)**; jobs
> `ExecuteRun(runId)` / `ResumeRun(runId)`. The execution *model* stands; only the
> technology changes.

- **Decision:** Agent runs execute as background jobs in a dedicated worker. The
  run is a persisted state machine in Postgres; the human-approval gate
  checkpoints state and ends the job; approval enqueues a separate resume job.
- **Context:** DL-005's human gate means a run pauses for arbitrary human time; a
  held HTTP request cannot model that. The project outcome names scalable
  operations; queue-from-commit-one is a requirement.
- **Options:** (A) background task in API process + persisted state; (B) queue +
  worker — **chosen**.
- **Rationale:** durability across restarts, horizontal worker scaling, and the
  pause/resume flow fall out of the same design.
- **Defensibility:** "A human-gated agent run can't be a request — it's a durable
  job that checkpoints at the gate and resumes on approval."
- **Trade-offs:** +worker process and job wiring vs. in-process simplicity —
  accepted as a requirement.
- **Success signal:** kill the worker mid-run after the checkpoint; approve; resume
  completes the run with no data loss.
- **Skill-spec notes:** jobs are exactly `ExecuteRun(runId)` and `ResumeRun(runId)`;
  all state passes through Postgres, never through job payloads; `AgentRun.Status`
  enum is the single source of run truth.

---

## DL-007 — Isolation: Postgres RLS as the data layer of a three-layer model

> Partially superseded by DL-015: session-variable wiring is an EF Core
> `DbConnectionInterceptor` fed by a request-scoped `IBrandContext`, not a FastAPI
> dependency. The RLS policies are identical Postgres; the guarantee is unchanged.

- **Decision:** RLS on every brand-scoped table with
  `current_setting('app.current_brand')` policies; transaction-scoped `set_config`.
  Combined with MinIO brand prefixes and Vault-Transit-encrypted credentials →
  three-layer isolation (data / storage / credentials), mapping 1:1 to DL-002.
- **Options:** (A) RLS — **chosen**; (B) app-layer scoped query layer with RLS as
  future hardening — rejected as the exact pattern DL-002 warns against relying on
  alone.
- **Defensibility:** "Leakage is prevented by the database, not by remembering a
  WHERE clause."
- **Trade-offs:** policy + interceptor wiring; pool-safety requires
  transaction-scoped `set_config(…, true)`.
- **Success signal:** an automated test seeds two brands and proves queries, storage
  paths, and token decrypts cannot cross scopes.
- **Skill-spec notes:** policies live in EF Core migrations (raw SQL); the EF
  interceptor is the only place brand scope is bound; pgvector tables carry the
  same policies.

---

## DL-008 — Frontend: Streamlit

> **SUPERSEDED by DL-014 (React/Next.js).** Retained for history (append-only rule).

- **Decision (historical):** Streamlit app for onboarding, approval gate, run/trace
  viewer.
- **Rationale (historical):** covered every demo-critical surface cheaply.

---

## DL-009 — Object storage: MinIO behind a `Storage` interface

> Partially superseded by DL-015: interface is C# `IStorageService`; `MinioStorage`
> uses the Minio .NET SDK. Decision unchanged.

- **Decision:** MinIO is the running implementation; brand-prefixed keys;
  S3-compatible API makes the cloud swap a config change.
- **Defensibility:** "Assets live outside the database, isolated by key scheme,
  behind an interface that makes S3 a settings change."
- **Success signal:** generated media lands under `brands/{brand_id}/…` and is
  served via presigned URLs.

---

## DL-010 — RAG in MVP: brand-knowledge CMS on pgvector, same Postgres

- **Decision:** RAG is MVP scope. The corpus is a per-brand, manager-editable
  knowledge base (guidelines, products, voice exemplars, past content) — CRUD'd
  through the API, chunked and embedded into pgvector in the same Postgres,
  retrieved to ground strategy/caption generation.
- **Context:** bootcamp expectations make RAG non-optional; the Week-8 multi-tenant
  CMS project is the proven pattern being reused.
- **Rationale:** same-Postgres pgvector puts embeddings under the existing RLS
  policies — one isolation surface; an external vector DB would create a second
  isolation problem for zero demo benefit.
- **Defensibility:** "The vectors live where the isolation lives — RAG inherits RLS
  instead of re-implementing tenancy in a second store."
- **Trade-offs:** pgvector over specialized vector DBs sacrifices nothing at this
  scale; chunking/rerank choices and golden retrieval sets are Phase 5/9 decisions.
  Embedding runtime resolved in DL-016.
- **Success signal:** captions for the demo brand visibly use retrieved brand facts
  absent from the prompt; a retrieval eval exists by Phase 9.
- **Skill-spec notes:** `IRetrievalService`; ingest pipeline = doc → chunks →
  embeddings, all rows brand-scoped; retrieval queries run under the RLS-bound
  DbContext.

---

## DL-011 — Secrets: Vault KV for app config, Vault Transit for brand tokens

> Partially superseded by DL-015: client is **VaultSharp**; KV binds into the
> **Options pattern**. The envelope-encryption design is unchanged.

- **Decision:** Vault is the single secrets authority with two mechanisms. KV backs
  typed settings for static app secrets. Per-brand Meta tokens are
  envelope-encrypted: ciphertext + metadata in the RLS-scoped `BrandMetaConnection`
  entity; Transit owns keys, decrypts on use, audits every decrypt. Vault runs
  dev-mode for the demo; hardening path documented.
- **Options:** (A) tokens as KV paths per brand — rejected: makes Vault a second
  tenant store outside RLS; (B) Transit + Postgres ciphertext — **chosen**;
  (C) plaintext config files only — rejected per security requirement.
- **Defensibility:** "Config is static and app-wide → KV; credentials are dynamic
  tenant data → encrypted in the tenant store with Vault holding the keys."
- **Trade-offs:** dev-mode Vault demonstrates the pattern, not production security —
  stated openly with the hardening checklist.
- **Success signal:** no plaintext secret in repo, image, DB, or logs; a token
  decrypt appears in Vault's audit output during a live-path call.
- **Skill-spec notes:** `ISecretsProvider` (`VaultProvider` / `EnvProvider` for
  tests); Transit key `brand-tokens`; decrypt only inside `IMetaIntegration` at call
  time, never cached to disk or logged.

---

## DL-012 — Queue technology: Arq

> **SUPERSEDED by DL-015 (Hangfire on PostgreSQL).** Retained for history.

- **Decision (historical):** Arq on Redis, async-native jobs.

---

## DL-013 — Build order: vertical slice first, depth in place

- **Decision:** stand up the full eight-service topology thin by Day 3 with one
  trivial end-to-end run, then deepen each layer without ever breaking the slice.
- **Rationale:** the dominant risk is not scalability but Day 8 arriving with
  half-wired services and no working loop; the slice converts that risk into "less
  depth somewhere," which is survivable.
- **Defensibility:** "The demo has been runnable since Day 3 — every day after that
  only added depth."
- **Success signal:** `docker compose up` + demo script completes on any day from
  Day 3 onward.

---

## DL-014 — Frontend: React/Next.js (supersedes DL-008)

- **Decision:** The frontend is a React/Next.js (TypeScript) application in its own
  folder and container, delivering the analytics dashboard, content-approval
  workflows, onboarding, and the run/trace viewer. All UI is React/Next.js.
- **Context:** the project plan mandates a React/Next.js dashboard with analytics
  and content-approval workflows. The architect-lead's call overrides the Phase-1
  Streamlit recommendation.
- **Rationale:** conforms to the project plan; React/Vite experience exists from
  prior projects; richer analytics + approval UX; stronger portfolio surface.
- **Defensibility:** "The dashboard is the product's face — built in React/Next.js
  per the project plan, with the analytics and approval workflows the brief calls
  for."
- **Trade-offs:** more frontend build effort than Streamlit vs. richer UX and plan
  conformance — accepted.
- **Success signal:** a reviewer completes onboard → run → approve → view trace
  through the React/Next.js UI and views the analytics dashboard.
- **Skill-spec notes:** `frontend/` is a Next.js app with its own Dockerfile and
  dependency set; it talks to the API over HTTP via a typed client in `lib/`; no
  business logic in the frontend; brand scope and all data come from the API.
  Backend-agnostic — unaffected by the .NET switch.

---

## DL-015 — Backend stack reversal: .NET (employer mandate)

- **Decision:** the backend is **.NET** (ASP.NET Core Web API, current LTS),
  reversing the Python/FastAPI freeze. Driven by an external hard constraint: the
  employer who originated the project enforces .NET. Every locked *property* (async,
  DI, typed boundaries, RLS isolation, queue+worker, durable checkpoint/resume,
  Vault secrets, MinIO storage, pgvector RAG, human gate) is preserved; the
  *technologies* map to .NET equivalents (see `stack-and-topology.md`).
- **Context:** the bootcamp taught Python; the prior freeze chose Python on
  toolchain-fit and MCP-maturity grounds. An employer mandate overrides those.
- **Defensibility:** "Same architecture, .NET idioms — the employer mandates .NET,
  so DI, typed boundaries, RLS, durable jobs, and the secrets model are all
  expressed in their native .NET form."
- **Trade-offs / risks (surfaced, not hidden):**
  1. **MCP maturity** — the .NET MCP SDK is younger than Python's; connecting to the
     Meta Ads MCP carries more integration risk. Mitigated by mock-first (DL-004):
     live Meta is off the critical path.
  2. **Embedding runtime** — resolved (DL-016): self-hosted nomic-embed-text-v1.5
     over HTTP via `IEmbeddingProvider`. No paid API, no in-process ONNX wiring;
     adds one container (eight services).
  3. **Standards docs** — the Python-specific bootcamp docs no longer apply
     verbatim; defend principles, not syntax.
- **Success signal:** the full mocked loop runs end-to-end on the .NET stack with
  the two-brand RLS leakage test passing.
- **Skill-spec notes:** one `Backend.sln`, layered Api/Worker/Core/Infrastructure;
  Hangfire job store in its own Postgres schema; RLS policies in EF migrations; all
  integrations behind C# interfaces with mocks; CI on mocks only.

---

## DL-016 — Embedding runtime: self-hosted nomic-embed-text-v1.5

- **Decision:** embeddings are generated by a self-hosted, open-source model —
  **nomic-embed-text-v1.5** — served over HTTP from a local model-server container
  (Ollama as the default runner; HF Text-Embeddings-Inference an alternative if a
  reranker is later co-hosted). The .NET app calls it via `IEmbeddingProvider`
  (`NomicEmbeddingProvider`). Resolves the open item in DL-010 / DL-015.
- **Options:** (A) ONNX Runtime in-process — rejected (tokenizer wiring cost);
  (B) paid hosted embedding API — rejected (cost, data leaves the box);
  (C) self-hosted open-source model over HTTP — **chosen**.
- **Rationale:** open-source + local = no per-token cost and no data egress;
  nomic-embed-text-v1.5 is a strong, small retrieval model that runs comfortably on
  the dev GPU; the API-call pattern keeps the model out of the .NET process while
  staying fully self-hosted.
- **Defensibility:** "Embeddings are a local open-source model behind an interface —
  free, private, and swappable for a hosted API by changing one implementation."
- **Trade-offs:** +1 container (eight services total); the model server must be
  healthy before ingest/retrieval — accepted; mockable in CI via `IEmbeddingProvider`.
- **Critical know-your-model notes:**
  - Requires task prefixes: `search_document:` when embedding corpus chunks,
    `search_query:` when embedding the query. Missing/mismatched prefixes silently
    degrade retrieval.
  - Native dimension 768 (Matryoshka-truncatable to 512/256/128/64); the pgvector
    column dimension must equal the chosen output dim (default 768) — set it in the
    EF migration.
  - Normalize embeddings; index with cosine distance (`vector_cosine_ops`).
- **Success signal:** ingest embeds corpus with `search_document:`, queries embed
  with `search_query:`, and retrieval returns brand-relevant chunks in the eval set.
- **Skill-spec notes:** `IEmbeddingProvider` with `NomicEmbeddingProvider` (HTTP) +
  a mock for CI; embedding dim is config-bound and must equal the pgvector column
  dim; reranker runtime remains a Phase 5 decision (the same local-server pattern
  can host a cross-encoder via TEI if chosen then).

---

## Upstream scope decisions (DL-001 … DL-005, context)

These were frozen in `Product_Identity_and_Capstone_Scope.md` and constrain the
architecture. They are AI/model boundaries unaffected by the .NET switch.

- **DL-001** — Claude orchestrates (MCP-native); Gemini is a media-generation tool
  behind `IMediaGenerationTool`. No other model orchestrates.
- **DL-002** — Demo a single DTC brand (specialty coffee roaster); enforce
  multi-brand isolation structurally from commit one.
- **DL-003** — MVP = images + captions for one brand, publish mocked. Video and live
  ads are advanced scope.
- **DL-004** — Meta integration is mocked-first behind `IMetaIntegration`; live Meta
  is a bonus swapped behind the same interface. The demo runs with zero live Meta.
- **DL-005** — Autonomous through ideation, generation, draft scheduling;
  **human-gated** at any `publish` action and any paid/ads action.
