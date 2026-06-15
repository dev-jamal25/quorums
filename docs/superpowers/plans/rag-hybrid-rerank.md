# RAG Hybrid Recall + Cross-Encoder Rerank + Multi-Query (Slice 3) Implementation Plan

> **For agentic workers:** Use superpowers:subagent-driven-development OR superpowers:executing-plans — **execution strategy is the owner's call** (see "Task dependencies & execution" below; Tasks 4/5/6 all mutate `PgVectorRetrieval` and are sequential, not independent). Steps use checkbox (`- [ ]`) syntax for tracking. Honour the `brand-knowledge-rag`, `dotnet-engineering-standards`, `claude-api`, and `marketing-agency-architecture` skills throughout. The `brand-knowledge-rag` skill is the **frozen design contract — re-decide nothing in it** (DL-024/025/026). Apply TDD (superpowers:test-driven-development) on every unit.

**Goal:** Turn on the three retrieval stages slice 2 shipped present-but-OFF — **sparse FTS recall + union**, **cross-encoder rerank + metadata blend**, and **multi-query expansion** — each independently config-gated behind the existing `RetrievalOptions` toggles, so the Phase-9 ablation flips a flag rather than editing code. All-toggles-off reproduces slice-2 dense-only behaviour byte-for-byte.

**Architecture:** Three stages fill the existing seam inside `PgVectorRetrieval : IRetrievalService` (`Retrieve(query, brandId, docType?, k)` stays the only public surface, DL-025):
- **S0 — Query transform (toggle `QueryTransformEnabled`):** `IQueryTransformer` → Haiku expands the original query to N variants (default 3, config-bound model string seeded from the current Haiku model). Variants widen recall only; the original is always pooled and the **reranker scores the pool against the original query**. Haiku unreachable → single-query fallback + `ToolError`, never a crash.
- **S1 — Sparse arm + union (toggle `SparseEnabled`):** Postgres FTS over the already-populated generated `search_vector` (`tsvector` + GIN built in slice 2 — **no new migration**), metadata-filtered by `docType` + brand via RLS, deduped-UNIONed with the existing dense arm into a top-N≈20 recall set. The FTS arm runs as **raw SQL on the same `BrandScope`-bound connection**, so `set_config('app.current_brand', …, true)` scopes it — never a manual `WHERE brand_id`.
- **S2 — Rerank + metadata blend (toggle `RerankEnabled`):** `IRerankProvider` → `tei-rerank` (`bge-reranker-v2-m3`, `/rerank`) fuses the dense∪sparse union and returns **pure** cross-encoder relevance (reranker-as-fusion; RRF k≈60 is the named fallback only). The metadata blend (relevance + performance/recency, config-bound weights) runs in `PgVectorRetrieval` → top-k≈5. tei-rerank unreachable → union-order fallback + `ToolError`, never a crash.

**Tech Stack:** .NET 10, EF Core 9.0.2 / Npgsql 9.0.2, `Pgvector` + `Pgvector.EntityFrameworkCore`, Postgres + pgvector (`pgvector/pgvector:pg16`), HF TEI (`bge-reranker-v2-m3` via `tei-rerank`), `Microsoft.Extensions.AI` `IChatClient` (Anthropic-backed, Haiku — the one Claude-call abstraction the generation slice will reuse), xUnit + Testcontainers.

---

## Context (The Why)

Slice 2 (branch `feat/rag-ingest-retrieval`, merged to `main`) shipped ingest + **dense** retrieval behind a **config-gated stage seam**: `PgVectorRetrieval` already runs S0/S1/S2 as named phases, but only the dense recall arm is wired; sparse, rerank, and query-transform are present-but-off via `RetrievalOptions`. The full four-stage hybrid pipeline (DL-025) is this slice. The schema for it already exists — the generated `search_vector` `tsvector` column self-populates and is GIN-indexed (slice-2 migration `KnowledgeVectorSchema`), and the performance-bearing `historical_post` seed corpus carries real `engagement_rate`/`ctr`/`audience_segment` — so **slice 3 adds no migration and no ingest change**. It is the pipeline behind `IRetrievalService` only.

**Current seam (read before building):**
- [PgVectorRetrieval.cs](backend/src/Infrastructure/Knowledge/PgVectorRetrieval.cs) — S0 currently `throw new NotSupportedException` when `QueryTransformEnabled`; S1 is `DenseRecallAsync` only (toggle `DenseEnabled`); S2 is `candidates.Take(topK)`. The outer `try/catch` turns any failure into `RetrievalResult([], false, ToolError("retrieval.failed", …, true))` (DL-022). Dense score = `1.0 - CosineDistance`.
- [RetrievalOptions.cs](backend/src/Infrastructure/Configuration/Options/RetrievalOptions.cs) — `QueryTransformEnabled` (off), `QueryVariants=3`, `DenseEnabled=true`, `SparseEnabled` (off), `RecallDepth=20`, `RerankEnabled` (off), `FinalK=5`. **No blend weights yet** — Task 2 adds them.
- [IRetrievalService.cs](backend/src/Core/Knowledge/IRetrievalService.cs) — `RetrievedChunk(ChunkId, DocId, Content, DocType, Facet, Score)` and `RetrievalResult(Chunks, Grounded, Error?)`.
- [KnowledgeChunk](backend/src/Core/Domain/KnowledgeChunk.cs) — carries `Embedding (Vector)`, `Metadata (jsonb string)`, `DocType`, `Facet`, `Content`; `search_vector` is a **generated, unmapped** column (read via raw SQL only — confirmed by the slice-2 spike).
- [KnowledgeChunkMetadata.cs](backend/src/Core/Knowledge/KnowledgeChunkMetadata.cs) — `EngagementRate`, `Ctr`, `AudienceSegment`, `Objective`, `Date`, `Source`, `IsCompetitor`, … (structured, never embedded).
- [CoffeeRoasterCorpus.cs](backend/src/Infrastructure/Knowledge/Seed/CoffeeRoasterCorpus.cs) — two `historical_post`s with real perf: *"Post - Pour Over Sunday"* (`EngagementRate=0.071, Ctr=0.034, AudienceSegment="enthusiasts"`) and *"Post - Espresso Tutorial"* (`0.052 / 0.028 / "beginners"`). These are the blend-boost test fixtures.
- [KnowledgeFixture.cs](backend/tests/IntegrationTests/Knowledge/KnowledgeFixture.cs) — `[Collection("Knowledge")]` shared container; `CreateRetrieval(brandId)` builds `new PgVectorRetrieval(db, _embeddings, Options.Create(new RetrievalOptions()))` (dense-only); `CreateSuperuserContext()` bypasses RLS for ownership checks; `BrandA`/`BrandB`/`BrandWithNoCorpus`.
- [KnowledgeServiceCollectionExtensions.cs](backend/src/Infrastructure/Knowledge/KnowledgeServiceCollectionExtensions.cs) — `AddKnowledge(config)`; `Embeddings:Mode` switch (`mock` vs typed-HttpClient `nomic`).
- [appsettings.json](backend/src/Api/appsettings.json) — already has `Reranker:Endpoint = "tei-rerank:80"`, the `Retrieval` toggle block, and `Embeddings`. [docker-compose.yml](docker-compose.yml) — `tei-rerank` (`BAAI/bge-reranker-v2-m3`, `/rerank`, host `8091:80`).

**Intended outcome:** with the toggles flipped on, retrieval runs the four-stage hybrid pipeline; with them off it is byte-identical to slice 2 (the existing `RagIsolationTests`/`DenseRelevanceTests` still pass unchanged). Six adversarial proofs are green: sparse-arm isolation, rerank-reorders + metadata-boost, ablation toggles, two degrade paths, CI mocks. All CLAUDE.md gates stay green.

## Frozen givens (from `brand-knowledge-rag` — do NOT re-decide)

- **Isolation is inherited, never manual** — on **both** arms. The FTS arm runs raw SQL on the `BrandScope`-bound connection; RLS scopes it. Never a hand-written `WHERE brand_id`. `docType` is a legitimate content filter.
- **One ranking authority.** The cross-encoder ranks; dense+sparse only recall. **No BM25/cosine score blending.** RRF (`Σ 1/(k+rank)`, k≈60) is the named fallback only.
- **Blend stays in `PgVectorRetrieval`.** `IRerankProvider` returns **pure** relevance; metadata touches the score only in the blend.
- **Metadata is never embedded.** Perf/recency/segment ride in the `jsonb`/typed columns and feed only the filter + blend.
- **Every stage is config-gated and independently toggleable**, and a disabled or failing stage **never crashes a run** (DL-022 — `ToolError`, not an exception into the graph).
- **All tuning knobs config-bound, never literals**: N, k, variant count, blend weights (α,β,γ,δ), recency half-life, reranker endpoint, query-transform model.
- **The query-transformer model string is config-bound, seeded from the current Haiku model at build time** — `claude-haiku-4-5` (authoritative current Haiku alias per the `claude-api` skill catalog, 2026-06-15), **never hardcoded in code or recalled from memory**.

## Out of scope (do NOT build here)

Generation-pipeline agents; the agent-graph wiring of retrieval (so no `targetAudienceSegment` caller input yet — see judgment call #2); publishing; any new EF migration; any ingest/chunker/embedding change. Slice 3 is the pipeline behind `IRetrievalService` only.

---

## Two judgment calls (surfaced for review — see the decisions, not buried)

### JC-1 — tsvector mapping: raw-SQL FTS arm (NOT an EF-mapped computed column)

**Decision:** query `search_vector` via **raw SQL** using an **`(id, ts_rank_cd)` projection** (`Database.SqlQueryRaw<…>`) → scoped entity re-read, leaving the column **unmapped** on the entity. The projection carries the FTS rank (needed when rerank is off — see below), which `FromSqlRaw<KnowledgeChunk>` cannot surface because the entity has no rank property.

**Authority for the raw SQL:** this read-only FTS over the unmapped `tsvector` is an **explicitly granted carve-out in `.claude/rules/infrastructure.md`** (added with this plan), NOT a self-authorized exception. The rule otherwise bans raw SQL; the carve-out permits it **only** for this read-only FTS read, **only on the brand-scoped connection** (so RLS still applies), and never sets a brand id in a `WHERE`. The plan does not grant itself the exception — the rule file does.

**Why / evidence:** the slice-2 spike already established that `search_vector` is a `GENERATED ALWAYS AS (to_tsvector('english', content)) STORED` column, **not mapped** on `KnowledgeChunk`, read via raw SQL (`EF.Property`/LINQ throws "property could not be found"). Mapping a pre-existing generated column risks EF's model differ emitting a spurious `AddColumn`/`AlterColumn` migration. **Hard acceptance bar: slice 3 adds no migration.** Task 1 re-confirms `dotnet ef migrations add <probe>` produces an **empty** `Up()` after the slice-3 C# changes (then the probe is discarded). The raw-SQL projection is the migration-free path and runs on the same RLS-bound connection (Task 1 proves the RLS scoping, doesn't assume it).

**Why carry `ts_rank_cd` (validation fix, item 4):** the union is unranked and the reranker is the sole ranking authority — so the FTS rank is irrelevant **when rerank is ON**. But the **rerank-OFF + sparse-ON** ablation cell exists (Phase-9), and with no rank the sparse-only result is ordered arbitrarily (every sparse candidate would otherwise carry score `0.0`) and the top-k cut is meaningless. Carrying `ts_rank_cd` as the candidate's `SparseScore` lets that ablation cell order by FTS rank — the lexical analogue of dense cosine order. Both the primary projection and the id-only fallback therefore carry `(id, rank)`, never bare ids.

### JC-2 — blend normalization (the additive form is skill/DL-025-specified; the judgment call is the normalization)

**Not re-decided here — specified by the skill (DL-025):** the blend is an **additive weighted sum** of per-`docType` terms. `brand-knowledge-rag/references/retrieval-pipeline.md §3` fixes exactly this form, and `§4` keeps it **separate from fusion** (the cross-encoder is the sole fusion/ranking authority; no BM25/cosine weight tuning). The form below is **transcribed from the skill, not chosen by this plan**:

```
historical_post : α·relNorm + β·perfNorm + γ·segmentMatch
market_intel    : α·relNorm + δ·recencyDecay
others          : α·relNorm        (α = 1)
```

- **The judgment call (what IS open):** the skill fixes the additive form but **not the normalization**. The genuinely-open sub-decisions I am surfacing are: **(a)** `relNorm = min-max(rerankRelevance over the candidate pool)` — normalize to `[0,1]` so the additive sum is meaningful whether TEI returns logits or sigmoid scores; **(b)** `perfNorm = min-max((engagement_rate + ctr)/2)` over the `historical_post` candidates in the pool; **(c)** `recencyDecay = 2^(-ageDays / RecencyHalfLifeDays)`. Multiplicative boost (`rel·(1+β·perf)`) and near-tie tie-breakers were rejected — they would re-decide the skill's frozen additive form, not the normalization. The blend never touches fusion (skill §4) — it runs only after the reranker, inside `PgVectorRetrieval`.
- **Self-ablating boundary:** defaults `α=1`, `β,γ,δ` configurable. With `β=γ=δ=0` the blend collapses to `relNorm` — i.e. **pure reranker order** — so the reranker stays pure and the blend itself is ablatable by zeroing weights. The metadata-boost test uses `β>0`; a control with `β=0` proves it reproduces pure-rerank order.
- **`segmentMatch` is provisioned-but-inert in slice 3** (`γ` default `0`): `IRetrievalService.Retrieve(query, brandId, docType?, k)` has **no audience-segment parameter** and agent-graph wiring is out of scope, so there is no target segment to match against yet. The blend computes `segmentMatch` when a target is supplied (slice 4 wiring), but slice 3's boost proof exercises the **performance** term (`β·perfNorm`) on the seed `historical_post`s, which *is* computable. **`recencyDecay` is both unit-tested (pure function) and exercised end-to-end** by a `market_intel` recency integration test against a dated seed `market_intel` doc added in Task 5 (validation fix, item 5 — owner-approved coverage, no longer a disclosed gap).

---

## File structure

**Core** (`backend/src/Core/`):
- Create `Knowledge/IRerankProvider.cs` — `IRerankProvider` + `RerankScore(int Index, double Relevance)`.
- Create `Knowledge/IQueryTransformer.cs` — `IQueryTransformer` (`Expand(query, variants)`).

**Infrastructure** (`backend/src/Infrastructure/`):
- Create `Knowledge/CrossEncoderRerankProvider.cs` — typed `HttpClient` → `tei-rerank` `/rerank`; pure relevance; failure → throws (caught into `ToolError` by `PgVectorRetrieval`).
- Create `Knowledge/DeterministicRerankProvider.cs` — CI mock: deterministic lexical-overlap relevance (reorders the union meaningfully; offline).
- Create `Knowledge/ChatQueryTransformer.cs` — injects `Microsoft.Extensions.AI.IChatClient` (Anthropic-backed; model from `QueryTransformOptions.Model`); N line-parsed variants; defensive fallback. (Named for the abstraction, not the vendor — this is the one Claude-call path the generation slice reuses, item 6.)
- Create `Knowledge/DeterministicQueryTransformer.cs` — CI mock: deterministic variants.
- Create `Knowledge/MetadataBlend.cs` — `internal` pure blend helper (JC-2); unit-tested in isolation, called by `PgVectorRetrieval`.
- Modify `Knowledge/PgVectorRetrieval.cs` — final ctor `(db, embeddings, rerank, transform, options)`; sparse FTS arm + union carrying `SparseScore` (S1); rerank + blend (S2); query-transform + variant pooling (S0); inner-catch degrade paths.
- Modify `Knowledge/KnowledgeServiceCollectionExtensions.cs` — register `IRerankProvider` (`Reranker:Mode` switch) and `IQueryTransformer` (`QueryTransform:Mode` switch) + the Anthropic-backed `IChatClient`.
- Modify `Knowledge/Seed/CoffeeRoasterCorpus.cs` — add one dated `market_intel` seed doc so the `δ·recencyDecay` arm gets end-to-end coverage (item 5; seed/dev change, not the ingest pipeline).
- Modify `Configuration/Options/RetrievalOptions.cs` — add nested `RetrievalBlendOptions Blend` (α,β,γ,δ, RecencyHalfLifeDays).
- Create `Configuration/Options/RerankerOptions.cs` — `Endpoint`, `Mode` (`tei`|`mock`).
- Create `Configuration/Options/QueryTransformOptions.cs` — `Model` (default `claude-haiku-4-5`), `Mode` (`chat`|`mock`).
- Modify `Configuration/OptionsServiceCollectionExtensions.cs` — register + validate `RerankerOptions`, `QueryTransformOptions`.
- Modify `backend/Directory.Packages.props` + `backend/src/Infrastructure/Infrastructure.csproj` — pin + reference `Microsoft.Extensions.AI` (abstractions) + an Anthropic-backed `IChatClient` provider package (NOT the raw `Anthropic` SDK — item 6).

**Api** (`backend/src/Api/`):
- Modify `appsettings.json` — add `Reranker:Mode`, `QueryTransform` section, `Retrieval:Blend` weights. (Toggles stay **off** by default → slice-2 parity is the shipped default; the ablation flips them.)

**Tests** (`backend/tests/`):
- Modify `IntegrationTests/Knowledge/KnowledgeFixture.cs` — overloads to build `PgVectorRetrieval` with custom `RetrievalOptions` + injected mock providers; keep the no-arg `CreateRetrieval(brandId)` (dense-only) so slice-2 tests compile unchanged.
- Create `IntegrationTests/Knowledge/SparseArmIsolationTests.cs` — `[Trait("Category","Isolation")]` — the critical new leakage proof on the FTS arm.
- Create `IntegrationTests/Knowledge/RerankAndBlendTests.cs` — rerank reorders + metadata boost.
- Create `IntegrationTests/Knowledge/AblationToggleTests.cs` — each stage independently toggleable; all-off = dense parity.
- Create `IntegrationTests/Knowledge/RetrievalDegradeTests.cs` — rerank-unreachable + transformer-unreachable → `ToolError`, no crash.
- Create `IntegrationTests/Knowledge/MarketIntelRecencyTests.cs` — `[Trait("Category","Isolation")]` — fresher `market_intel` outranks stale via `δ·recencyDecay` (item 5 end-to-end coverage).
- Create `IntegrationTests/Knowledge/RerankProviderContractTests.cs` — `CrossEncoderRerankProvider` request shape over a recording handler (no network) + opt-in `[Trait("Category","LiveRerank")]` live test.
- Create `UnitTests/Knowledge/MetadataBlendTests.cs` — perf boost + recency decay on the pure function.
- Create `UnitTests/Knowledge/DeterministicRerankProviderTests.cs`, `UnitTests/Knowledge/DeterministicQueryTransformerTests.cs`.

---

## Confirmed contracts (Task 1 spike — run 2026-06-15)

> **Status:** the two load-bearing keystones (FTS/RLS, migration-free) are **CONFIRMED live** against the real `pgvector/pgvector:pg16` container. The two network-dependent items (tei-rerank `/rerank`, `IChatClient` package) are **DOCUMENTED, live-verification pending** — a flaky connection (`tls: bad record MAC`) stalled tei-rerank's ~2.3 GB weight download and blocks new NuGet restores. Re-verify both when the network is stable (Step 1 = `curl localhost:8091/rerank`; Step 4 = restore + `dotnet build -warnaserror`). Tasks 2+ may proceed against the confirmed FTS/RLS shape now.

- **tei-rerank `/rerank` contract — DOCUMENTED (live pending):** per the HF Text-Embeddings-Inference rerank API: `POST /rerank` body `{"query": "<original query>", "texts": ["<doc0>", "<doc1>", …], "raw_scores": false}` → `200` with `[{"index": <int>, "score": <double>}, …]` **sorted by score descending**; `index` is the position in the request `texts[]`; with `raw_scores:false` the score is the sigmoid-normalized relevance in `[0,1]` (set `raw_scores:true` for raw logits). The `CrossEncoderRerankProvider` (Task 3) re-keys results back to input order. **Live re-check pending:** `curl -s localhost:8091/rerank -d '{"query":"floral light roast","texts":["…yirgacheffe…","…espresso…"],"raw_scores":false}'`.
- **FTS query shape — ✅ CONFIRMED live:** `SELECT id AS "Id", ts_rank_cd(search_vector, websearch_to_tsquery('english', {0}))::float8 AS "Rank" FROM knowledge_chunks WHERE search_vector @@ websearch_to_tsquery('english', {0}) [AND doc_type = {1}] ORDER BY "Rank" DESC LIMIT {N}` via `db.Database.SqlQueryRaw<FtsHit>(sql, term, n)` where `private sealed record FtsHit(Guid Id, double Rank);`. **Confirmed:** binds and materializes; the **`::float8` cast is REQUIRED** (`ts_rank_cd` returns `float4`/`real`; without the cast it won't map to `double Rank`); column aliases `"Id"`/`"Rank"` map to the record by name; then a scoped `db.KnowledgeChunks.Where(c => ids.Contains(c.Id))` re-read materializes entities, re-attaching `Rank` as `SparseScore`. (`FromSqlRaw<KnowledgeChunk>` is rejected: it cannot surface the computed `rank` — JC-1.)
- **Migration-free proof — ✅ CONFIRMED live:** from `backend/`, `dotnet ef migrations add ProbeNoOp -p src/Infrastructure -s src/Api` produced an **empty `Up()` and `Down()`** with the entity unchanged — the unmapped generated `search_vector` does not trigger a spurious migration (JC-1 baseline holds). **Cleanup note:** `dotnet ef migrations remove` needs a live DB (the design-time placeholder conn `127.0.0.1:5432` fails offline), so remove the probe by deleting the two `…_ProbeNoOp.cs`/`.Designer.cs` files and `git restore` the snapshot (its only diff is a cosmetic CRLF→LF touch). Re-run after the slice-3 C# changes to keep the bar green.
- **Sparse-arm RLS — ✅ CONFIRMED live:** the `SqlQueryRaw` FTS read on the `BrandScope`-bound `AppDbContext` connection runs inside the transaction-scoped `set_config('app.current_brand', …, true)` and **is RLS-scoped** — the spike bound Brand A, FTS-queried `"roast"` (present in both brands' identical corpus), and got **zero Brand-B chunks** (ownership cross-checked via the superuser context; vacuity guard confirmed B genuinely has matching chunks). The scoped entity re-read on the same connection is RLS-filtered too. The `.claude/rules/infrastructure.md` carve-out authorizes exactly this read-only FTS use.
- **Union/dedup shape — ✅ confirmed:** dense top-N (existing LINQ) ∪ sparse top-N (`SqlQueryRaw<FtsHit>` projection → scoped re-read) deduped by `chunk.Id` into one unranked recall set. Candidate carrier carries `Id, DocId, Content, DocType, Facet, Metadata`, plus **`DenseScore` and `SparseScore`** (max-merged on dedup) for the rerank-off ordering (item 4).
- **`IChatClient` (Anthropic-backed) — DOCUMENTED (live pending):** plan to pin `Microsoft.Extensions.AI` (the `IChatClient` abstraction) + an Anthropic-backed provider that builds an `IChatClient`. The transformer injects `IChatClient` and calls `await chat.GetResponseAsync(prompt, new ChatOptions { ModelId = "claude-haiku-4-5", MaxOutputTokens = 256 }, ct)`, then reads `response.Text`. **Live re-check pending (network):** confirm the exact provider package + builder registration, that `ChatOptions.ModelId` takes the bare config string, and that it binds on .NET 10 (`dotnet build -warnaserror`). Compile-only — CI never calls Claude (the mock transformer covers CI/ablation). The single Claude-call abstraction the generation slice reuses (item 6).

---

## Task dependencies & execution (NOT independent — owner picks the strategy)

These tasks are **not** parallel-independent — they share `PgVectorRetrieval.cs` and chain:

```
Task 1 (spike) ─▶ Task 2 (config) ─▶ Task 3 (IRerankProvider)
                                          │
                                          ▼
                 Task 4 (sparse arm + FINAL ctor) ─▶ Task 5 (rerank + blend) ─▶ Task 6 (S0 transform) ─▶ Task 7 (ablation + gates)
```

- **Tasks 4, 5, 6 all mutate `PgVectorRetrieval.cs`.** Task 4 sets the final ctor `(db, embeddings, rerank, transform, options)` once; Tasks 5 and 6 fill the rerank/blend and the S0 stages into that ctor. Running them out of order, or in parallel against the same file, will collide.
- Task 2 (config types) gates Tasks 3/5/6; Task 3 (the rerank provider + mock) gates Tasks 4/5.
- **Execution strategy is the owner's call** — subagent-per-task (with the dependency order enforced and a rebase between tasks) **or** one linear session. The plan does **not** bake in subagents; pick per your review cadence.

---

## Task 0: Land this plan, STOP

Branch: **`feat/rag-hybrid-rerank`**, off the freshly-merged `main`.

- [ ] **Step 1:** Write this plan to `docs/superpowers/plans/rag-hybrid-rerank.md` (matches the slice-2 `rag-ingest-retrieval.md` naming — no date prefix).
- [ ] **Step 2:** Create the branch and commit the plan:

```bash
git checkout -b feat/rag-hybrid-rerank
git add docs/superpowers/plans/rag-hybrid-rerank.md
git commit -m "docs(rag): slice-3 hybrid recall + rerank + multi-query implementation plan"
```

- [ ] **Step 3:** **STOP.** Do not begin Task 1 until the user says "start".

---

## Task 1: Spike the keystones (throwaway — confirm against REAL containers/packages, record, delete)

**Files:** Test (throwaway, deleted at task end): `backend/tests/IntegrationTests/Knowledge/Slice3SpikeTests.cs`. Probe migration (created then removed): none committed.

> **Run status (2026-06-15):** Steps 2 & 3 (FTS/RLS + migration-free) ✅ **CONFIRMED live** against the real pgvector container — see the Confirmed-contracts cheat-sheet. Steps 1 & 4 (tei-rerank `/rerank`, `IChatClient` package) are **DEFERRED — network-blocked** (`tls: bad record MAC` stalled the tei-rerank weight download / NuGet restores); their contracts are **documented** in the cheat-sheet and must be **live-verified before Task 3 (rerank provider) and Task 6 (transformer) ship**. The throwaway spike was deleted; no probe migration committed.

- [ ] **Step 1: tei-rerank `/rerank` contract — against the live container. (DEFERRED — network-blocked; contract documented, live verify before Task 3.)** Bring up `tei-rerank` (`docker compose up tei-rerank`; allow `start_period: 120s` for first-run weights) and probe:

```bash
curl -s http://localhost:8091/rerank \
  -H 'Content-Type: application/json' \
  -d '{"query":"floral light roast","texts":["Ethiopia Yirgacheffe floral jasmine bergamot light roast","Sunrise espresso blend chocolate caramel medium-dark"],"raw_scores":false}' | jq .
```

Record the exact request keys and the response array shape (`[{index, score}]`, sorted desc) + score range into the **Confirmed contracts** cheat-sheet.

- [x] **Step 2: FTS query + RLS on the BrandScope connection — throwaway Testcontainer spike. ✅ CONFIRMED (2/2 passed).** Write `Slice3SpikeTests.cs` that: applies migrations to a `pgvector/pgvector:pg16` container, seeds two brands with chunks whose `content` shares an FTS term, binds Brand A via `BrandScope`, then runs the assumed `FromSqlRaw` FTS query on the bound context and asserts (a) it returns A's matching chunk, (b) it returns **zero** B chunks (RLS), (c) `ts_rank_cd` ordering works. Reuse the `KnowledgeFixture` connection/role pattern (least-privilege `app_user`, superuser cross-check).

```csharp
// keystone of the spike — confirm the (id, rank) projection + RLS scoping on the bound connection.
// Project id + ts_rank_cd (item 4: the rank must survive), THEN re-read entities scoped.
var hits = await db.Database
    .SqlQueryRaw<FtsHit>(
        "SELECT id AS \"Id\", ts_rank_cd(search_vector, websearch_to_tsquery('english', {0})) AS \"Rank\" " +
        "FROM knowledge_chunks " +
        "WHERE search_vector @@ websearch_to_tsquery('english', {0}) " +
        "ORDER BY \"Rank\" DESC LIMIT {1}", term, 20)
    .ToListAsync();
var ids = hits.Select(h => h.Id).ToList();
var chunks = await db.KnowledgeChunks.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync();
// Assert: chunks all belong to Brand A (RLS), B excluded, even though B has matching content;
// and hits carry a descending Rank (ts_rank_cd ordering works).
// where: private sealed record FtsHit(Guid Id, double Rank);
```

`FromSqlRaw<KnowledgeChunk>` is **rejected** (JC-1): it cannot surface the computed `ts_rank_cd` column into the entity, and item 4 needs the rank. The `SqlQueryRaw<FtsHit>` projection → scoped re-read is the primary path. **Confirm and record** the exact projection that binds (column-alias casing for `SqlQueryRaw`) in the cheat-sheet (Task 4 uses it verbatim).

- [x] **Step 3: Migration-free proof (JC-1). ✅ CONFIRMED (empty Up()/Down()).** With the entity unchanged (no `search_vector` mapping), run (from `backend/`) `dotnet ef migrations add ProbeNoOp -p src/Infrastructure -s src/Api`, confirm `Up()`/`Down()` are empty, then remove the probe (offline: delete the two `…_ProbeNoOp.cs`/`.Designer.cs` files + `git restore` the snapshot, since `dotnet ef migrations remove` needs a live DB). Recorded in the cheat-sheet.
- [ ] **Step 4: `IChatClient` (Anthropic-backed) shape. (DEFERRED — network-blocked; documented, live verify before Task 6.)** In the spike, add `Microsoft.Extensions.AI` + an Anthropic-backed `IChatClient` provider to `Directory.Packages.props` + reference them; confirm the `IChatClient` builder registration compiles and `await chat.GetResponseAsync(prompt, new ChatOptions { ModelId = "claude-haiku-4-5", MaxOutputTokens = 256 }, ct)` + `response.Text` bind (compile-only — no live call; CI never calls Claude). Record the confirmed provider package + call shape + pinned versions. (Item 6: standardize on `IChatClient`, not the raw `Anthropic` SDK.)
- [ ] **Step 5: Reconcile + record.** Update the **Confirmed contracts** cheat-sheet with every confirmed shape. If anything moved, update Tasks 2–7 samples in-place.
- [ ] **Step 6: Delete the spike.** `git rm backend/tests/IntegrationTests/Knowledge/Slice3SpikeTests.cs`. Ensure no probe migration remains.
- [ ] **Step 7: Commit.**

```bash
git add backend/Directory.Packages.props backend/src/Infrastructure/Infrastructure.csproj docs/superpowers/plans/rag-hybrid-rerank.md
git commit -m "build(rag): pin Microsoft.Extensions.AI IChatClient; record tei-rerank/FTS/RLS contracts (spike, then removed)"
```

---

## Task 2: Config surface — blend weights, reranker + query-transform options, validation, appsettings

**Files:**
- Modify: `backend/src/Infrastructure/Configuration/Options/RetrievalOptions.cs`
- Create: `backend/src/Infrastructure/Configuration/Options/RerankerOptions.cs`, `backend/src/Infrastructure/Configuration/Options/QueryTransformOptions.cs`
- Modify: `backend/src/Infrastructure/Configuration/OptionsServiceCollectionExtensions.cs`, `backend/src/Api/appsettings.json`
- Test: `backend/tests/UnitTests/Knowledge/RetrievalOptionsBindingTests.cs`

- [ ] **Step 1: Add the nested blend weights to `RetrievalOptions`** (config-bound, never literals; α=1 + β=γ=δ=0 ⇒ pure rerank order):

```csharp
// append to RetrievalOptions
/// <summary>S2 metadata blend weights (DL-025). Defaults: α=1 relevance; β/δ boost; γ inert
/// (no target segment until agent wiring). With β=γ=δ=0 the blend collapses to pure rerank order.</summary>
public RetrievalBlendOptions Blend { get; init; } = new();

// new file alongside, or nested in the same file:
public sealed class RetrievalBlendOptions
{
    public double Alpha { get; init; } = 1.0;          // relevance weight
    public double Beta { get; init; } = 0.3;           // historical_post performance
    public double Gamma { get; init; }                 // historical_post segment_match (inert in slice 3)
    public double Delta { get; init; } = 0.3;          // market_intel recency
    public double RecencyHalfLifeDays { get; init; } = 30.0;
}
```

- [ ] **Step 2: Create `RerankerOptions`** (`Endpoint` matches the existing appsettings key):

```csharp
namespace Backend.Infrastructure.Configuration.Options;

/// <summary>Cross-encoder reranker settings (DL-024/025). Endpoint is host:port only; the app
/// prepends the scheme. Mode: "tei" (real tei-rerank) or "mock" (deterministic, offline). CI uses mock.</summary>
public sealed class RerankerOptions
{
    public const string SectionName = "Reranker";
    public string Endpoint { get; init; } = "tei-rerank:80";
    public string Mode { get; init; } = "tei";
}
```

- [ ] **Step 3: Create `QueryTransformOptions`** (model seeded from the current Haiku alias):

```csharp
namespace Backend.Infrastructure.Configuration.Options;

/// <summary>Multi-query expander settings (S0). Model is config-bound, seeded at build time from
/// the current Haiku model (claude-haiku-4-5) — never hardcoded in code or recalled. Mode: "haiku"
/// (real Anthropic API) or "mock" (deterministic, offline). CI uses mock.</summary>
public sealed class QueryTransformOptions
{
    public const string SectionName = "QueryTransform";
    public string Model { get; init; } = "claude-haiku-4-5";
    public string Mode { get; init; } = "haiku";
}
```

- [ ] **Step 4: Register + validate** in `OptionsServiceCollectionExtensions.AddValidatedAppOptions` (after `RetrievalOptions`):

```csharp
services.AddValidatedOptions<RerankerOptions>(configuration, RerankerOptions.SectionName);
services.AddValidatedOptions<QueryTransformOptions>(configuration, QueryTransformOptions.SectionName);
```

- [ ] **Step 5: appsettings.json** — extend `Reranker`, add `QueryTransform`, add `Retrieval:Blend` (toggles stay **off** → slice-2 parity is the default):

```jsonc
"Reranker": { "Endpoint": "tei-rerank:80", "Mode": "tei" },
"QueryTransform": { "Model": "claude-haiku-4-5", "Mode": "haiku" },
"Retrieval": {
  "QueryTransformEnabled": false, "QueryVariants": 3,
  "DenseEnabled": true, "SparseEnabled": false, "RecallDepth": 20,
  "RerankEnabled": false, "FinalK": 5,
  "Blend": { "Alpha": 1.0, "Beta": 0.3, "Gamma": 0.0, "Delta": 0.3, "RecencyHalfLifeDays": 30.0 }
}
```

- [ ] **Step 6: Binding test** — assert `Retrieval:Blend` binds and defaults hold:

```csharp
[Fact]
public void Retrieval_blend_weights_bind_from_config()
{
    var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Retrieval:Blend:Beta"] = "0.5", ["Retrieval:Blend:RecencyHalfLifeDays"] = "14",
    }).Build();
    var opts = cfg.GetSection("Retrieval").Get<RetrievalOptions>()!;
    Assert.Equal(0.5, opts.Blend.Beta);
    Assert.Equal(14.0, opts.Blend.RecencyHalfLifeDays);
    Assert.Equal(1.0, opts.Blend.Alpha);        // default preserved
}
```

- [ ] **Step 7: Gates.** `dotnet build backend/Backend.sln -warnaserror`; `dotnet format backend/Backend.sln --verify-no-changes`; run the binding test. Expected PASS.
- [ ] **Step 8: Commit.** `git commit -m "feat(rag): config surface for blend weights + reranker + query-transform options"`

---

## Task 3: IRerankProvider — CrossEncoderRerankProvider (HTTP→tei-rerank) + deterministic CI mock

**Files:**
- Create: `backend/src/Core/Knowledge/IRerankProvider.cs`, `backend/src/Infrastructure/Knowledge/CrossEncoderRerankProvider.cs`, `backend/src/Infrastructure/Knowledge/DeterministicRerankProvider.cs`
- Test: `backend/tests/UnitTests/Knowledge/DeterministicRerankProviderTests.cs`, `backend/tests/IntegrationTests/Knowledge/RerankProviderContractTests.cs` (reuses slice-2's `RecordingHttpMessageHandler`)

- [ ] **Step 1: Interface + score record** (pure relevance — no metadata, DL-025):

```csharp
namespace Backend.Core.Knowledge;

/// <summary>A pure cross-encoder relevance score for the doc at <see cref="Index"/> in the input list.</summary>
public sealed record RerankScore(int Index, double Relevance);

/// <summary>Scores (query, doc) pairs with the bge-reranker cross-encoder (DL-025). Returns PURE
/// relevance — the metadata blend is PgVectorRetrieval's job, never the provider's.</summary>
public interface IRerankProvider
{
    Task<IReadOnlyList<RerankScore>> RerankAsync(
        string query, IReadOnlyList<string> documents, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Failing test for the deterministic mock** — lexical overlap drives relevance, and the mock must be able to **reorder** an input list (so S2 tests can prove the reranker runs):

```csharp
public sealed class DeterministicRerankProviderTests
{
    [Fact]
    public async Task Relevance_ranks_lexically_closer_doc_higher()
    {
        var p = new DeterministicRerankProvider();
        var scores = await p.RerankAsync("floral light roast", new[]
        {
            "chocolate caramel medium-dark espresso",      // index 0 — far
            "floral jasmine bergamot light roast washed",  // index 1 — near
        });
        var top = scores.OrderByDescending(s => s.Relevance).First();
        Assert.Equal(1, top.Index);                        // proves it reorders away from input order
        Assert.Equal(2, scores.Count);                      // one score per input doc
    }
}
```

- [ ] **Step 3: Run, expect FAIL.**
- [ ] **Step 4: Implement the deterministic mock** (offline, deterministic; relevance = Jaccard-ish token overlap):

```csharp
using Backend.Core.Knowledge;

namespace Backend.Infrastructure.Knowledge;

/// <summary>Offline, deterministic cross-encoder stand-in for CI (DL-025). Relevance = normalized
/// query/doc token overlap, so a lexically closer doc scores higher and the union order is
/// meaningfully reordered — enough to prove the reranker engages without a model server.</summary>
public sealed class DeterministicRerankProvider : IRerankProvider
{
    public Task<IReadOnlyList<RerankScore>> RerankAsync(
        string query, IReadOnlyList<string> documents, CancellationToken cancellationToken = default)
    {
        var q = Tokens(query);
        var scores = new List<RerankScore>(documents.Count);
        for (var i = 0; i < documents.Count; i++)
        {
            var d = Tokens(documents[i]);
            var overlap = q.Count == 0 ? 0.0 : q.Intersect(d).Count() / (double)q.Count;
            scores.Add(new RerankScore(i, overlap));
        }
        return Task.FromResult<IReadOnlyList<RerankScore>>(scores);
    }

    private static HashSet<string> Tokens(string text) =>
        text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new string(t.Where(char.IsLetterOrDigit).ToArray()))
            .Where(t => t.Length > 0).ToHashSet();
}
```

- [ ] **Step 5: Run, expect PASS.**
- [ ] **Step 6: Implement the HTTP provider** (typed `HttpClient` → `tei-rerank` `/rerank`; pure relevance; throws on transport failure — `PgVectorRetrieval` maps it to `ToolError`). **Use the Task-1-confirmed request/response shape:**

```csharp
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Backend.Core.Knowledge;

namespace Backend.Infrastructure.Knowledge;

/// <summary>bge-reranker-v2-m3 via HF TEI (tei-rerank, /rerank). Returns PURE cross-encoder
/// relevance (DL-025). Transport failures throw and are mapped to a ToolError by the caller (DL-022).</summary>
public sealed class CrossEncoderRerankProvider : IRerankProvider
{
    private readonly HttpClient _http;
    public CrossEncoderRerankProvider(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<RerankScore>> RerankAsync(
        string query, IReadOnlyList<string> documents, CancellationToken cancellationToken = default)
    {
        if (documents.Count == 0) return [];
        using var resp = await _http.PostAsJsonAsync(
            "/rerank", new RerankRequest(query, [.. documents], false), cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var ranked = await resp.Content
            .ReadFromJsonAsync<List<RerankResponseItem>>(cancellationToken).ConfigureAwait(false) ?? [];
        return ranked.Select(r => new RerankScore(r.Index, r.Score)).ToList();
    }

    private sealed record RerankRequest(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("texts")] string[] Texts,
        [property: JsonPropertyName("raw_scores")] bool RawScores);

    private sealed record RerankResponseItem(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("score")] double Score);
}
```

- [ ] **Step 7: Contract test over a recording handler** (request shape, no network — CI never calls TEI), plus an opt-in live test mirroring slice-2's `LiveEmbeddings` pattern:

```csharp
public sealed class RerankProviderContractTests
{
    private static CrossEncoderRerankProvider Provider(RecordingHttpMessageHandler h) =>
        new(new HttpClient(h) { BaseAddress = new Uri("http://tei-rerank") });

    [Fact]
    public async Task Rerank_posts_query_and_texts_to_rerank_endpoint()
    {
        var h = new RecordingHttpMessageHandler("[{\"index\":1,\"score\":0.9},{\"index\":0,\"score\":0.1}]");
        var scores = await Provider(h).RerankAsync("floral roast", new[] { "espresso", "yirgacheffe floral" });
        Assert.Contains("\"query\":\"floral roast\"", h.RequestBodies[0]);
        Assert.Contains("\"texts\":[", h.RequestBodies[0]);
        Assert.Equal(1, scores.OrderByDescending(s => s.Relevance).First().Index);
    }

    [Fact(Skip = "Opt-in live test: requires a running tei-rerank. Remove Skip to run locally.")]
    [Trait("Category", "LiveRerank")]
    public async Task Real_tei_rerank_ranks_matching_doc_first()
    {
        var endpoint = Environment.GetEnvironmentVariable("Reranker__Endpoint") ?? "localhost:8091";
        using var http = new HttpClient { BaseAddress = new Uri($"http://{endpoint}") };
        var scores = await new CrossEncoderRerankProvider(http).RerankAsync(
            "floral light roast",
            new[] { "chocolate caramel medium-dark espresso", "floral jasmine bergamot light roast" });
        Assert.Equal(1, scores.OrderByDescending(s => s.Relevance).First().Index);
    }
}
```

- [ ] **Step 8: DI registration** in `AddKnowledge` (`Reranker:Mode` switch, mirroring `Embeddings:Mode`):

```csharp
var rerankMode = (configuration["Reranker:Mode"] ?? "tei").Trim().ToLowerInvariant();
if (rerankMode == "mock")
{
    services.AddSingleton<IRerankProvider, DeterministicRerankProvider>();
}
else
{
    var endpoint = configuration["Reranker:Endpoint"] ?? "tei-rerank:80";
    services.AddHttpClient<IRerankProvider, CrossEncoderRerankProvider>(client =>
    {
        client.BaseAddress = new Uri($"http://{endpoint}");
        client.Timeout = TimeSpan.FromSeconds(30);
    });
}
```

- [ ] **Step 9: Gates + commit.** Build/format/tests green. `git commit -m "feat(rag): IRerankProvider — tei-rerank cross-encoder + deterministic CI mock"`

---

## Task 4: Sparse FTS arm + union in PgVectorRetrieval (RLS-scoped, the critical isolation proof)

This task rewrites the `PgVectorRetrieval` ctor to the **final** signature (injecting the rerank + transform providers now, even though they are used in Tasks 5–7) so the ctor changes exactly once. It adds the sparse arm + union behind `SparseEnabled`. Rerank/blend/query-transform stay as the existing no-op seam for now.

**Files:**
- Modify: `backend/src/Infrastructure/Knowledge/PgVectorRetrieval.cs`, `backend/src/Infrastructure/Knowledge/KnowledgeServiceCollectionExtensions.cs`, `backend/tests/IntegrationTests/Knowledge/KnowledgeFixture.cs`
- Test: `backend/tests/IntegrationTests/Knowledge/SparseArmIsolationTests.cs`

- [ ] **Step 1: Final ctor + internal candidate carrier.** Inject `IRerankProvider` + `IQueryTransformer`; add an internal candidate type that carries the metadata the blend needs:

```csharp
public PgVectorRetrieval(
    AppDbContext db, IEmbeddingProvider embeddings, IRerankProvider rerank,
    IQueryTransformer transform, IOptions<RetrievalOptions> options)
{
    _db = db; _embeddings = embeddings; _rerank = rerank; _transform = transform; _options = options.Value;
}

// internal recall candidate — carries Metadata for S2's blend (still clean: Content is the only text).
// DenseScore = 1.0 - cosine distance (0 for sparse-only hits); SparseScore = ts_rank_cd (0 for dense-only
// hits). Both are kept so the rerank-OFF path can order by Max(DenseScore, SparseScore) — item 4.
private sealed record Candidate(
    Guid Id, Guid DocId, string Content, DocType DocType, KnowledgeFacet? Facet, string? Metadata,
    double DenseScore, double SparseScore);
```

- [ ] **Step 2: Failing sparse-arm isolation test** — the critical new attack surface. Cross-brand leakage proof on the FTS arm, with the vacuity guard (B genuinely has FTS-matching content; ownership via the **unscoped superuser** lookup, not a hand-added brand filter):

```csharp
[Trait("Category", "Isolation")]
[Collection("Knowledge")]
public sealed class SparseArmIsolationTests
{
    private readonly KnowledgeFixture _fixture;
    public SparseArmIsolationTests(KnowledgeFixture fixture) => _fixture = fixture;

    // sparse-only options: dense OFF, sparse ON — isolates the FTS arm as the sole recall path
    private static RetrievalOptions SparseOnly() =>
        new() { DenseEnabled = false, SparseEnabled = true, RerankEnabled = false, QueryTransformEnabled = false };

    [Fact]
    public async Task Sparse_arm_under_brand_A_returns_zero_brand_B_chunks()
    {
        var (db, scope, retrieval) = _fixture.CreateRetrieval(_fixture.BrandA, SparseOnly());
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            // "roast" is a brand-distinctive term present in BOTH brands' identical corpus.
            var result = await retrieval.Retrieve("roast style and brand voice", _fixture.BrandA, docType: null, k: 20);
            Assert.NotEmpty(result.Chunks);     // FTS actually matched something for A

            await using var admin = _fixture.CreateSuperuserContext(); // bypasses RLS — sees all brands
            // Vacuity guard: Brand B really has FTS-matching content, so only RLS keeps it out of A's result.
            Assert.True(await admin.KnowledgeChunks.AsNoTracking().CountAsync(c => c.BrandId == _fixture.BrandB) > 0);

            foreach (var hit in result.Chunks)
            {
                var owner = await admin.KnowledgeChunks.AsNoTracking()
                    .Where(c => c.Id == hit.ChunkId).Select(c => c.BrandId).FirstAsync();
                Assert.Equal(_fixture.BrandA, owner);   // zero B leakage on the sparse arm
            }
        }
    }

    [Fact]
    public async Task Sparse_arm_under_brand_B_returns_zero_brand_A_chunks()
    {
        var (db, scope, retrieval) = _fixture.CreateRetrieval(_fixture.BrandB, SparseOnly());
        await using (db)
        {
            await using var handle = await scope.BeginAsync();
            var result = await retrieval.Retrieve("roast style and brand voice", _fixture.BrandB, docType: null, k: 20);
            Assert.NotEmpty(result.Chunks);
            await using var admin = _fixture.CreateSuperuserContext();
            Assert.True(await admin.KnowledgeChunks.AsNoTracking().CountAsync(c => c.BrandId == _fixture.BrandA) > 0);
            foreach (var hit in result.Chunks)
            {
                var owner = await admin.KnowledgeChunks.AsNoTracking()
                    .Where(c => c.Id == hit.ChunkId).Select(c => c.BrandId).FirstAsync();
                Assert.Equal(_fixture.BrandB, owner);
            }
        }
    }
}
```

- [ ] **Step 3: Fixture overload** so the test compiles — keep the no-arg `CreateRetrieval(brandId)` (dense-only) for slice-2 tests, add an options+providers overload defaulting to the deterministic mocks:

```csharp
public (AppDbContext Db, IBrandScope Scope, IRetrievalService Retrieval) CreateRetrieval(
    Guid brandId, RetrievalOptions? options = null,
    IRerankProvider? rerank = null, IQueryTransformer? transform = null)
{
    var db = CreateDbContext(AppUserConnectionString);
    var brandContext = new BrandContext();
    brandContext.Bind(brandId);
    var scope = new BrandScope(db, brandContext);
    var retrieval = new PgVectorRetrieval(
        db, _embeddings,
        rerank ?? new DeterministicRerankProvider(),
        transform ?? new DeterministicQueryTransformer(),   // lands in Task 6; until then, a stub mock
        Options.Create(options ?? new RetrievalOptions()));
    return (db, scope, retrieval);
}
```

> If `DeterministicQueryTransformer` does not yet exist when this task runs, inline a trivial pass-through stub in the fixture and replace it in Task 6. Keep the no-arg overload (`CreateRetrieval(brandId)`) intact.

- [ ] **Step 4: Run, expect FAIL** (`SparseEnabled` path not implemented).
- [ ] **Step 5: Implement the sparse arm + union** in `PgVectorRetrieval`, using the **Task-1-confirmed** `SqlQueryRaw<FtsHit>` projection shape. Recall = dense ∪ sparse, deduped by chunk id (merging each arm's score), unranked:

```csharp
private async Task<List<Candidate>> RecallAsync(IReadOnlyList<string> variants, string? docType, int n)
{
    var merged = new Dictionary<Guid, Candidate>();
    foreach (var variant in variants)
    {
        if (_options.DenseEnabled)
            foreach (var c in await DenseArmAsync(variant, docType, n).ConfigureAwait(false))
                Merge(merged, c);
        if (_options.SparseEnabled)
            foreach (var c in await SparseArmAsync(variant, docType, n).ConfigureAwait(false))
                Merge(merged, c);
    }
    return merged.Values.ToList();   // unranked recall set; S2 ranks
}

// Dedup by chunk id, keeping the max of each arm's score so a chunk found by BOTH arms carries
// its dense cosine AND its FTS rank (item 4 — needed for the rerank-OFF ordering).
private static void Merge(Dictionary<Guid, Candidate> acc, Candidate c) =>
    acc[c.Id] = acc.TryGetValue(c.Id, out var e)
        ? e with { DenseScore = Math.Max(e.DenseScore, c.DenseScore), SparseScore = Math.Max(e.SparseScore, c.SparseScore) }
        : c;

private sealed record FtsHit(Guid Id, double Rank);

private async Task<List<Candidate>> SparseArmAsync(string query, string? docType, int n)
{
    // Read-only FTS on the BrandScope-bound connection → RLS scopes it (carve-out in
    // .claude/rules/infrastructure.md; never a manual WHERE brand_id). docType is an explicit
    // content filter, parameterized. Project (id, ts_rank_cd) so the FTS rank survives (item 4).
    const string cols = "SELECT id AS \"Id\", ts_rank_cd(search_vector, websearch_to_tsquery('english', {0})) AS \"Rank\" " +
                        "FROM knowledge_chunks WHERE search_vector @@ websearch_to_tsquery('english', {0}) ";
    var sql = docType is null
        ? cols + "ORDER BY \"Rank\" DESC LIMIT {1}"
        : cols + "AND doc_type = {2} ORDER BY \"Rank\" DESC LIMIT {1}";

    var hits = docType is null
        ? await _db.Database.SqlQueryRaw<FtsHit>(sql, query, n).ToListAsync().ConfigureAwait(false)
        : await _db.Database.SqlQueryRaw<FtsHit>(sql, query, n,
              Enum.Parse<DocType>(docType, ignoreCase: true).ToString()).ToListAsync().ConfigureAwait(false);
    if (hits.Count == 0) return [];

    var rank = hits.ToDictionary(h => h.Id, h => h.Rank);
    var ids = rank.Keys.ToList();
    // Scoped entity re-read (also RLS-bound) for Content/Metadata the blend needs.
    var chunks = await _db.KnowledgeChunks.AsNoTracking()
        .Where(c => ids.Contains(c.Id)).ToListAsync().ConfigureAwait(false);
    return chunks.Select(c => new Candidate(
        c.Id, c.KnowledgeDocId, c.Content, c.DocType, c.Facet, c.Metadata,
        DenseScore: 0.0, SparseScore: rank[c.Id])).ToList();
}
```

> Refactor the existing `DenseRecallAsync` into `DenseArmAsync(variant, docType, n)` returning `List<Candidate>` (carry `Metadata`; `DenseScore = 1.0 - distance`, `SparseScore: 0.0`). The S2 cut stays `candidates.Take(topK)` projecting `Candidate → RetrievedChunk` (use `DenseScore` until Task 5 wires rerank). **Critical:** when `docType` enum→string is compared in SQL it must match the EF `HasConversion<string>()` storage — the public `docType` arrives as the enum **name** (`Enum.Parse<DocType>(docType, ignoreCase:true)`, matching slice-2's dense arm) and `.ToString()` yields the stored PascalCase (Task-1 confirms, e.g. `"Product"`).

- [ ] **Step 6: DI** — sparse needs no new registration (uses `_db`). Verify `AddKnowledge` still resolves `PgVectorRetrieval` with the new ctor (rerank + transform already registered in Task 3 / Task 6 — register a temporary mock `IQueryTransformer` in Task 6; if Task 6 not yet done, register `DeterministicQueryTransformer` stub now so DI resolves).
- [ ] **Step 7: Run, expect PASS** (both isolation facts). `dotnet test --filter "FullyQualifiedName~SparseArmIsolationTests"`.
- [ ] **Step 8: Gates** — build `-warnaserror`, format, **`dotnet test --filter Category=Isolation`** (the multi-tenant contract — must stay green; now covers the sparse arm). Commit.

`git commit -m "feat(rag): sparse FTS recall arm + dense∪sparse union (RLS-scoped, no migration)"`

---

## Task 5: Rerank + metadata blend in PgVectorRetrieval (S2 ranking authority) + degrade path

**Files:**
- Create: `backend/src/Infrastructure/Knowledge/MetadataBlend.cs`
- Modify: `backend/src/Infrastructure/Knowledge/PgVectorRetrieval.cs`, `backend/src/Infrastructure/Knowledge/Seed/CoffeeRoasterCorpus.cs` (Step 8 market_intel docs)
- Test: `backend/tests/UnitTests/Knowledge/MetadataBlendTests.cs`, `backend/tests/IntegrationTests/Knowledge/RerankAndBlendTests.cs`, `backend/tests/IntegrationTests/Knowledge/RetrievalDegradeTests.cs` (rerank half), `backend/tests/IntegrationTests/Knowledge/MarketIntelRecencyTests.cs`

- [ ] **Step 1: Failing unit tests for the pure blend** (JC-2 — additive weighted sum; perf boost + recency decay):

```csharp
public sealed class MetadataBlendTests
{
    private static readonly RetrievalBlendOptions W = new() { Alpha = 1.0, Beta = 0.3, Gamma = 0.0, Delta = 0.3, RecencyHalfLifeDays = 30 };

    [Fact]
    public void Historical_post_with_higher_performance_outscores_a_near_tie()
    {
        var now = DateTimeOffset.UtcNow;
        var high = "{\"EngagementRate\":0.071,\"Ctr\":0.034}";
        var low  = "{\"EngagementRate\":0.052,\"Ctr\":0.028}";
        // equal relNorm (near tie from the reranker) ⇒ perf term decides
        var sHigh = MetadataBlend.Score(relNorm: 0.8, perfNorm: 1.0, segmentMatch: 0.0, recencyDecay: 0.0, DocType.HistoricalPost, W);
        var sLow  = MetadataBlend.Score(relNorm: 0.8, perfNorm: 0.0, segmentMatch: 0.0, recencyDecay: 0.0, DocType.HistoricalPost, W);
        Assert.True(sHigh > sLow);
        _ = (high, low, now);
    }

    [Fact]
    public void Beta_zero_reproduces_pure_relevance_order()
    {
        var w0 = W with { Beta = 0, Delta = 0, Gamma = 0 };
        Assert.Equal(0.8, MetadataBlend.Score(0.8, 1.0, 0.0, 0.0, DocType.HistoricalPost, w0));
    }

    [Fact]
    public void Market_intel_fresher_intel_outscores_stale()
    {
        var fresh = MetadataBlend.Score(0.8, 0, 0, recencyDecay: 1.0, DocType.MarketIntel, W);
        var stale = MetadataBlend.Score(0.8, 0, 0, recencyDecay: 0.1, DocType.MarketIntel, W);
        Assert.True(fresh > stale);
    }
}
```

- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement the pure blend** (additive weighted sum; per-`docType`; α=1 + zeros ⇒ pure relevance):

```csharp
using Backend.Core.Domain;
using Backend.Infrastructure.Configuration.Options;

namespace Backend.Infrastructure.Knowledge;

/// <summary>The S2 metadata blend (DL-025, JC-2). Additive weighted sum on normalized terms; the
/// reranker stays pure (IRerankProvider) — this is the ONLY place metadata touches the score.
/// With β=γ=δ=0 it collapses to relNorm (pure rerank order).</summary>
internal static class MetadataBlend
{
    public static double Score(
        double relNorm, double perfNorm, double segmentMatch, double recencyDecay,
        DocType docType, RetrievalBlendOptions w) => docType switch
    {
        DocType.HistoricalPost => w.Alpha * relNorm + w.Beta * perfNorm + w.Gamma * segmentMatch,
        DocType.MarketIntel => w.Alpha * relNorm + w.Delta * recencyDecay,
        _ => w.Alpha * relNorm,                       // α = 1 baseline
    };

    public static double RecencyDecay(DateTimeOffset? date, DateTimeOffset now, double halfLifeDays)
    {
        if (date is null || halfLifeDays <= 0) return 0.0;
        var ageDays = Math.Max(0.0, (now - date.Value).TotalDays);
        return Math.Pow(2.0, -ageDays / halfLifeDays);
    }
}
```

- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Wire S2 into `PgVectorRetrieval`** — rerank the union (pure relevance), min-max normalize `rel` + `perf` over the pool, blend, cut top-k. Degrade on rerank failure → union order + `ToolError`:

```csharp
private async Task<(List<RetrievedChunk> Ranked, ToolError? Degrade)> RankAsync(
    string originalQuery, List<Candidate> candidates, int topK)
{
    if (!_options.RerankEnabled)
    {
        // Rerank off: order by best-available recall score. Dense-only → cosine (slice-2 parity,
        // SparseScore 0); sparse-only → ts_rank (DenseScore 0); both → the stronger signal. Item 4.
        var byRecall = candidates.OrderByDescending(RecallScore).Take(topK)
            .Select(c => ToChunk(c, RecallScore(c))).ToList();
        return (byRecall, null);
    }

    IReadOnlyList<RerankScore> scores;
    try
    {
        // Reranker scores the pool against the ORIGINAL query (variants only widened recall).
        scores = await _rerank.RerankAsync(originalQuery, candidates.Select(c => c.Content).ToList()).ConfigureAwait(false);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        // Degrade-don't-crash (DL-022): fall back to recall-score order, surface a ToolError.
        var fallback = candidates.OrderByDescending(RecallScore).Take(topK)
            .Select(c => ToChunk(c, RecallScore(c))).ToList();
        return (fallback, new ToolError("rerank.failed", ex.Message, true));
    }

    var rel = scores.ToDictionary(s => s.Index, s => s.Relevance);
    var relMin = rel.Values.Min(); var relMax = rel.Values.Max();
    var perfRaw = candidates.Select(Performance).ToList();
    var perfMin = perfRaw.Where(p => p.HasValue).Select(p => p!.Value).DefaultIfEmpty(0).Min();
    var perfMax = perfRaw.Where(p => p.HasValue).Select(p => p!.Value).DefaultIfEmpty(0).Max();
    var now = DateTimeOffset.UtcNow;

    var ranked = candidates.Select((c, i) =>
    {
        var relNorm = Normalize(rel.GetValueOrDefault(i, relMin), relMin, relMax);
        var perfNorm = perfRaw[i] is double p ? Normalize(p, perfMin, perfMax) : 0.0;
        var recency = MetadataBlend.RecencyDecay(MetadataOf(c)?.Date, now, _options.Blend.RecencyHalfLifeDays);
        var score = MetadataBlend.Score(relNorm, perfNorm, segmentMatch: 0.0, recency, c.DocType, _options.Blend);
        return ToChunk(c, score);
    }).OrderByDescending(x => x.Score).Take(topK).ToList();

    return (ranked, null);
}

private static double Normalize(double v, double min, double max) => max <= min ? 1.0 : (v - min) / (max - min);
private static double RecallScore(Candidate c) => Math.Max(c.DenseScore, c.SparseScore);   // item 4
private static RetrievedChunk ToChunk(Candidate c, double score) =>
    new(c.Id, c.DocId, c.Content, c.DocType, c.Facet, score);
private static double? Performance(Candidate c)
{
    var m = MetadataOf(c);
    if (m?.EngagementRate is null && m?.Ctr is null) return null;
    return ((m.EngagementRate ?? 0) + (m.Ctr ?? 0)) / 2.0;
}
private static KnowledgeChunkMetadata? MetadataOf(Candidate c) =>
    c.Metadata is null ? null : JsonSerializer.Deserialize<KnowledgeChunkMetadata>(c.Metadata);
```

Then refactor `Retrieve` so it `RecallAsync → RankAsync` and surfaces the degrade `ToolError` on `RetrievalResult.Error` while still returning the ranked chunks (degrade, don't crash).

- [ ] **Step 6: Failing integration test — rerank reorders + metadata boosts a high-performing historical_post.** Use a rerank mock that produces a near-tie between the two seed posts so the **β·perf** term decides; a `β=0` control reproduces pure-rerank order:

```csharp
[Trait("Category", "Isolation")]
[Collection("Knowledge")]
public sealed class RerankAndBlendTests
{
    private readonly KnowledgeFixture _fixture;
    public RerankAndBlendTests(KnowledgeFixture fixture) => _fixture = fixture;

    // Mock that returns a deliberate order DIFFERENT from dense cosine, proving the reranker runs,
    // and a near-tie between the two historical posts so the perf blend is the tie-breaker.
    private sealed class TieRerank : IRerankProvider
    {
        public Task<IReadOnlyList<RerankScore>> RerankAsync(string q, IReadOnlyList<string> docs, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RerankScore>>(docs.Select((_, i) => new RerankScore(i, 0.9)).ToList());
    }

    [Fact]
    public async Task Rerank_on_with_perf_blend_boosts_the_higher_engagement_post()
    {
        var opts = new RetrievalOptions { DenseEnabled = true, SparseEnabled = true, RerankEnabled = true,
            Blend = new RetrievalBlendOptions { Alpha = 1, Beta = 0.5, Delta = 0 } };
        var (db, scope, retrieval) = _fixture.CreateRetrieval(_fixture.BrandA, opts, rerank: new TieRerank());
        await using (db)
        {
            await using var handle = await scope.BeginAsync();
            var result = await retrieval.Retrieve("a slow ritual brewing coffee", _fixture.BrandA, docType: "HistoricalPost", k: 2);
            // "Pour Over Sunday" (eng 0.071) must outrank "Espresso Tutorial" (eng 0.052) on the perf blend.
            Assert.Equal(_fixture.PourOverSundayChunkId, result.Chunks[0].ChunkId);
        }
    }

    [Fact]
    public async Task Beta_zero_reproduces_pure_rerank_order_not_the_perf_boost()
    {
        var opts = new RetrievalOptions { RerankEnabled = true, Blend = new RetrievalBlendOptions { Alpha = 1, Beta = 0, Delta = 0 } };
        var (db, scope, retrieval) = _fixture.CreateRetrieval(_fixture.BrandA, opts, rerank: new TieRerank());
        await using (db)
        {
            await using var handle = await scope.BeginAsync();
            var result = await retrieval.Retrieve("a slow ritual brewing coffee", _fixture.BrandA, docType: "HistoricalPost", k: 2);
            Assert.Equal(2, result.Chunks.Count);   // both returned; with β=0 the perf term cannot reorder
        }
    }
}
```

> Add a `PourOverSundayChunkId` helper to `KnowledgeFixture` mirroring the existing `BrandAProductChunkId` pattern (`DeterministicGuid.From(DeterministicGuid.From(BrandA, "Post - Pour Over Sunday"), "0")`).

- [ ] **Step 7: Failing degrade test (rerank half)** — tei-rerank unreachable → union-order fallback + `ToolError`, no exception:

```csharp
[Collection("Knowledge")]
public sealed class RetrievalDegradeTests
{
    private readonly KnowledgeFixture _fixture;
    public RetrievalDegradeTests(KnowledgeFixture fixture) => _fixture = fixture;

    private sealed class ThrowingRerank : IRerankProvider
    {
        public Task<IReadOnlyList<RerankScore>> RerankAsync(string q, IReadOnlyList<string> d, CancellationToken ct = default)
            => throw new HttpRequestException("tei-rerank unreachable");
    }

    [Fact]
    public async Task Rerank_unreachable_degrades_to_union_order_with_toolerror()
    {
        var opts = new RetrievalOptions { DenseEnabled = true, RerankEnabled = true };
        var (db, scope, retrieval) = _fixture.CreateRetrieval(_fixture.BrandA, opts, rerank: new ThrowingRerank());
        await using (db)
        {
            await using var handle = await scope.BeginAsync();
            var result = await retrieval.Retrieve(_fixture.BrandAProductQuery, _fixture.BrandA, docType: "product", k: 3);
            Assert.NotEmpty(result.Chunks);                  // union-order fallback, no crash
            Assert.NotNull(result.Error);
            Assert.Equal("rerank.failed", result.Error!.Code);
        }
    }
}
```

- [ ] **Step 8: market_intel recency coverage (validation item 5).** Add two dated `market_intel` seed docs to `CoffeeRoasterCorpus.Specs` (one fresh, one ~2 years stale) sharing an FTS term, then prove the `δ·recencyDecay` blend surfaces the fresher one end-to-end:

```csharp
// CoffeeRoasterCorpus.Specs — append (Date drives δ·recencyDecay):
new(DocType.MarketIntel, null, "Intel - Specialty Trend 2026",
    "Specialty single-origin demand is climbing; pour-over and light roast lead the trend.",
    "intel/trend-2026.md",
    new KnowledgeChunkMetadata { Source = "trade-report", Date = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero) }),
new(DocType.MarketIntel, null, "Intel - Specialty Trend 2024",
    "Specialty single-origin demand was climbing; pour-over and light roast led the trend.",
    "intel/trend-2024.md",
    new KnowledgeChunkMetadata { Source = "trade-report", Date = new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero) }),
```

```csharp
[Trait("Category", "Isolation")]
[Collection("Knowledge")]
public sealed class MarketIntelRecencyTests
{
    private readonly KnowledgeFixture _fixture;
    public MarketIntelRecencyTests(KnowledgeFixture fixture) => _fixture = fixture;

    // Equal relevance from the reranker → the δ·recencyDecay term is the sole tie-breaker.
    private sealed class TieRerank : IRerankProvider
    {
        public Task<IReadOnlyList<RerankScore>> RerankAsync(string q, IReadOnlyList<string> docs, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RerankScore>>(docs.Select((_, i) => new RerankScore(i, 0.9)).ToList());
    }

    [Fact]
    public async Task Fresher_market_intel_outranks_stale_via_recency_decay()
    {
        var opts = new RetrievalOptions { DenseEnabled = true, SparseEnabled = true, RerankEnabled = true,
            Blend = new RetrievalBlendOptions { Alpha = 1, Beta = 0, Delta = 0.5, RecencyHalfLifeDays = 365 } };
        var (db, scope, retrieval) = _fixture.CreateRetrieval(_fixture.BrandA, opts, rerank: new TieRerank());
        await using (db)
        {
            await using var handle = await scope.BeginAsync();
            // docType is the EF enum-name ("MarketIntel"), matching slice-2's Enum.Parse; "market_intel" would throw.
            var result = await retrieval.Retrieve("specialty single origin trend", _fixture.BrandA, docType: "MarketIntel", k: 2);
            Assert.Equal(_fixture.MarketIntelFreshChunkId, result.Chunks[0].ChunkId);   // 2026 intel beats 2024
        }
    }
}
```

> Add a `MarketIntelFreshChunkId` helper to `KnowledgeFixture`: `DeterministicGuid.From(DeterministicGuid.From(BrandA, "Intel - Specialty Trend 2026"), "0")`. Re-seeding both brands with the two extra docs keeps the isolation tests' vacuity guard valid.

- [ ] **Step 9: Run all four, expect PASS.** Build/format/`Category=Isolation` green. Commit.

`git commit -m "feat(rag): cross-encoder rerank + metadata blend (S2) + recency coverage + rerank degrade path"`

---

## Task 6: IQueryTransformer — IChatClient (Haiku) multi-query expander + deterministic CI mock (S0) + degrade path

**Files:**
- Create: `backend/src/Core/Knowledge/IQueryTransformer.cs`, `backend/src/Infrastructure/Knowledge/ChatQueryTransformer.cs`, `backend/src/Infrastructure/Knowledge/DeterministicQueryTransformer.cs`
- Modify: `backend/src/Infrastructure/Knowledge/PgVectorRetrieval.cs`, `backend/src/Infrastructure/Knowledge/KnowledgeServiceCollectionExtensions.cs`
- Test: `backend/tests/UnitTests/Knowledge/DeterministicQueryTransformerTests.cs`, `backend/tests/IntegrationTests/Knowledge/RetrievalDegradeTests.cs` (Haiku half), `backend/tests/IntegrationTests/Knowledge/AblationToggleTests.cs` (S0 toggle)

- [ ] **Step 1: Interface** (default 3 variants, config-bound count):

```csharp
namespace Backend.Core.Knowledge;

/// <summary>S0 multi-query expansion (DL-025). One query → N variants that widen recall only;
/// the reranker still scores the pool against the original. Config-gated; off → single query.</summary>
public interface IQueryTransformer
{
    Task<IReadOnlyList<string>> ExpandAsync(string query, int variants, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Failing test for the deterministic mock** (deterministic, offline, includes paraphrase-like variants):

```csharp
public sealed class DeterministicQueryTransformerTests
{
    [Fact]
    public async Task Expand_returns_requested_count_deterministically()
    {
        var t = new DeterministicQueryTransformer();
        var a = await t.ExpandAsync("light roast notes", 3);
        var b = await t.ExpandAsync("light roast notes", 3);
        Assert.Equal(3, a.Count);
        Assert.Equal(a, b);                 // deterministic
        Assert.Contains(a, v => v.Contains("light roast notes")); // original signal preserved
    }
}
```

- [ ] **Step 3: Implement the deterministic mock** (suffix-paraphrase, deterministic):

```csharp
using Backend.Core.Knowledge;

namespace Backend.Infrastructure.Knowledge;

/// <summary>Offline deterministic multi-query expander for CI. Produces stable paraphrase-like
/// variants so the S0 ablation runs without calling Haiku.</summary>
public sealed class DeterministicQueryTransformer : IQueryTransformer
{
    private static readonly string[] Lenses = ["", " overview", " details", " examples", " guidance"];

    public Task<IReadOnlyList<string>> ExpandAsync(string query, int variants, CancellationToken ct = default)
    {
        var n = Math.Max(1, variants);
        var list = Enumerable.Range(0, n).Select(i => (query + Lenses[i % Lenses.Length]).Trim()).ToList();
        return Task.FromResult<IReadOnlyList<string>>(list);
    }
}
```

- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Implement the `IChatClient`-backed transformer** (model from `QueryTransformOptions.Model` — config-bound, seeded `claude-haiku-4-5`; **never** a literal). It injects `Microsoft.Extensions.AI.IChatClient` (Anthropic-backed) — the single Claude-call abstraction the generation slice reuses (item 6), not the raw `Anthropic` SDK. Line-parsed variants, defensive fallback to the single query. Use the **Task-1-confirmed** call shape:

```csharp
using Backend.Core.Knowledge;
using Backend.Infrastructure.Configuration.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Backend.Infrastructure.Knowledge;

/// <summary>Multi-query expander backed by Microsoft.Extensions.AI IChatClient (Anthropic/Haiku, S0).
/// The model id is config-bound (QueryTransformOptions.Model), seeded from the current Haiku model
/// at build time — never a literal here. Parse failures or a short reply degrade to the single
/// original query (caller adds it to the pool regardless), so S0 never crashes a run (DL-022).</summary>
public sealed class ChatQueryTransformer : IQueryTransformer
{
    private readonly IChatClient _chat;
    private readonly string _model;

    public ChatQueryTransformer(IChatClient chat, IOptions<QueryTransformOptions> qt)
    {
        _chat = chat;
        _model = qt.Value.Model;
    }

    public async Task<IReadOnlyList<string>> ExpandAsync(string query, int variants, CancellationToken ct = default)
    {
        var n = Math.Max(1, variants);
        var prompt =
            $"Rewrite the search query below as {n} alternative phrasings that surface the same intent " +
            $"with different vocabulary. Output exactly {n} lines, one phrasing per line, no numbering.\n\nQuery: {query}";

        var response = await _chat.GetResponseAsync(
            prompt,
            new ChatOptions { ModelId = _model, MaxOutputTokens = 256 },   // config string (Task-1 confirms)
            cancellationToken: ct).ConfigureAwait(false);

        var variantsOut = response.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0).Take(n).ToList();
        return variantsOut.Count > 0 ? variantsOut : [query];   // defensive — never empty
    }
}
```

- [ ] **Step 6: Wire S0 into `PgVectorRetrieval`** — when `QueryTransformEnabled`, expand to variants, **always include the original**, dedup; degrade on failure to `[query]` + `ToolError`. The reranker still scores against the original:

```csharp
private async Task<(IReadOnlyList<string> Variants, ToolError? Degrade)> VariantsAsync(string query)
{
    if (!_options.QueryTransformEnabled) return (new[] { query }, null);
    try
    {
        var expanded = await _transform.ExpandAsync(query, _options.QueryVariants).ConfigureAwait(false);
        // Always pool the original so a bad paraphrase set never loses it.
        var set = new List<string> { query };
        set.AddRange(expanded.Where(v => !string.Equals(v, query, StringComparison.OrdinalIgnoreCase)));
        return (set, null);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return (new[] { query }, new ToolError("querytransform.failed", ex.Message, true)); // single-query fallback
    }
}
```

Update `Retrieve` to: `VariantsAsync → RecallAsync(variants) → RankAsync(originalQuery, …)`, and surface whichever `ToolError` occurred (S0 or S2) on `RetrievalResult.Error`. **Remove the slice-2 `throw new NotSupportedException` S0 stub.**

- [ ] **Step 7: DI registration** in `AddKnowledge` (`QueryTransform:Mode` switch). Register the Anthropic-backed `IChatClient` + the transformer only in `chat` mode:

```csharp
var qtMode = (configuration["QueryTransform:Mode"] ?? "chat").Trim().ToLowerInvariant();
if (qtMode == "mock")
{
    services.AddSingleton<IQueryTransformer, DeterministicQueryTransformer>();
}
else
{
    // The single Claude-call path (item 6): Anthropic-backed Microsoft.Extensions.AI IChatClient.
    // ApiKey from AnthropicOptions (Vault/secret); model id is config-bound on the call (ChatOptions.ModelId).
    services.AddSingleton<IChatClient>(sp =>
    {
        var apiKey = sp.GetRequiredService<IOptions<AnthropicOptions>>().Value.ApiKey;
        return AnthropicChatClient.Create(apiKey);   // Task-1 records the exact provider builder + package
    });
    services.AddSingleton<IQueryTransformer, ChatQueryTransformer>();
}
```

- [ ] **Step 8: Failing degrade test (Haiku half)** — Haiku unreachable → single-query fallback + `ToolError`, no crash:

```csharp
[Fact]
public async Task Haiku_unreachable_degrades_to_single_query_with_toolerror()
{
    var opts = new RetrievalOptions { DenseEnabled = true, QueryTransformEnabled = true, RerankEnabled = false };
    var (db, scope, retrieval) = _fixture.CreateRetrieval(_fixture.BrandA, opts, transform: new ThrowingTransform());
    await using (db)
    {
        await using var handle = await scope.BeginAsync();
        var result = await retrieval.Retrieve(_fixture.BrandAProductQuery, _fixture.BrandA, docType: "product", k: 3);
        Assert.NotEmpty(result.Chunks);                         // ran on the single original query
        Assert.Equal("querytransform.failed", result.Error!.Code);
    }
}
// where ThrowingTransform.ExpandAsync throws HttpRequestException.
```

- [ ] **Step 9: Run, expect PASS.** Build/format/`Category=Isolation` green. Commit.

`git commit -m "feat(rag): Haiku multi-query expander (S0) + deterministic mock + query-transform degrade"`

---

## Task 7: Ablation parity sweep + slice-2 regression + final gates

The Phase-9 ablation precondition (DL-025): each stage independently toggleable; all-off = dense-only (slice-2 parity); each-on engages. This task proves the seam, not a cosmetic flag, and runs the full gate set.

**Files:**
- Create: `backend/tests/IntegrationTests/Knowledge/AblationToggleTests.cs`
- Verify (unchanged, must still pass): `RagIsolationTests.cs`, `DenseRelevanceTests.cs`, `EmptyRetrievalTests.cs`, `IngestIdempotencyTests.cs`, `PrefixCorrectnessTests.cs`, `KnowledgeSchemaTests.cs`

- [ ] **Step 1: Ablation matrix test** — all-off parity + each-stage-on engages, no crash on any configuration:

```csharp
[Trait("Category", "Isolation")]
[Collection("Knowledge")]
public sealed class AblationToggleTests
{
    private readonly KnowledgeFixture _fixture;
    public AblationToggleTests(KnowledgeFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task All_stages_off_reproduces_slice2_dense_only_top_chunk()
    {
        // Default RetrievalOptions = QueryTransform off, Sparse off, Rerank off → dense-only.
        var (db, scope, retrieval) = _fixture.CreateRetrieval(_fixture.BrandA);   // no-arg overload (slice-2 path)
        await using (db)
        {
            await using var handle = await scope.BeginAsync();
            var result = await retrieval.Retrieve(_fixture.BrandAProductQuery, _fixture.BrandA, "product", 3);
            Assert.True(result.Grounded);
            Assert.Equal(_fixture.BrandAProductChunkId, result.Chunks[0].ChunkId);  // identical to slice-2
            Assert.Null(result.Error);
        }
    }

    [Theory]
    [InlineData(true, false, false)]   // S0 only
    [InlineData(false, true, false)]   // sparse arm only
    [InlineData(false, false, true)]   // rerank only
    [InlineData(true, true, true)]     // full hybrid
    public async Task Each_toggle_combination_runs_without_crashing(bool s0, bool sparse, bool rerank)
    {
        var opts = new RetrievalOptions { QueryTransformEnabled = s0, DenseEnabled = true,
            SparseEnabled = sparse, RerankEnabled = rerank };
        var (db, scope, retrieval) = _fixture.CreateRetrieval(_fixture.BrandA, opts);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();
            var result = await retrieval.Retrieve(_fixture.BrandAProductQuery, _fixture.BrandA, "product", 3);
            Assert.NotEmpty(result.Chunks);   // a sane result set; no exception into the graph
        }
    }
}
```

- [ ] **Step 2: Run the ablation tests, expect PASS.**
- [ ] **Step 3: Slice-2 regression** — run the unchanged slice-2 suite and confirm parity:

```bash
dotnet test backend/Backend.sln --filter "FullyQualifiedName~RagIsolationTests|FullyQualifiedName~DenseRelevanceTests|FullyQualifiedName~EmptyRetrievalTests|FullyQualifiedName~IngestIdempotencyTests|FullyQualifiedName~PrefixCorrectnessTests"
```

Expected PASS (all-off default keeps them byte-identical). If any fails, the seam diverged from slice-2 behaviour — fix before continuing.

- [ ] **Step 4: Full Code Verification Checklist (CLAUDE.md).** Run and read output:
  1. `dotnet build backend/Backend.sln -warnaserror`
  2. `dotnet format backend/Backend.sln --verify-no-changes`
  3. `dotnet test backend/Backend.sln`
  4. **`dotnet test backend/Backend.sln --filter Category=Isolation`** (now covers dense **and** sparse arms — must pass)
  5. `gitleaks detect` clean (no Anthropic key in code/logs/config; `Anthropic:ApiKey` stays a secret)
  6. **No new migration**: `git status` shows zero files under `Persistence/Migrations/`; re-confirm `dotnet ef migrations add ProbeNoOp …` is empty then remove it.
- [ ] **Step 5: Commit + fill Output Summary.**

`git commit -m "test(rag): ablation toggle matrix + slice-2 parity regression; slice-3 gates green"`

---

## Output Summary (fill at completion)

- [ ] Smoke evidence (full `docker compose` stack, toggles flipped on):
  - `POST /brands` → `brandId`; seed corpus; `Retrieval:SparseEnabled/RerankEnabled/QueryTransformEnabled=true`.
  - Retrieve a brand-distinctive query → top-k differs from dense-only order (rerank engaged); high-engagement `historical_post` surfaces above a near-tie (blend boost).
  - Cross-brand: brand A retrieval returns zero brand-B chunks on **both** arms.
  - tei-rerank stopped → retrieval still returns union-order results + `rerank.failed` ToolError (no crash). Bad Anthropic key → `querytransform.failed`, single-query results.
  - `/health` → Healthy.
- [ ] Confirmed-contracts cheat-sheet finalized (tei-rerank `/rerank` shape; FTS `SqlQueryRaw<FtsHit>` projection form; migration-free proof; `IChatClient` call shape + pinned versions).
- [ ] All CLAUDE.md gates green; **no new migration**.
```