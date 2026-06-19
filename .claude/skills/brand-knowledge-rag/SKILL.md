---
name: brand-knowledge-rag
description: Implements the brand-knowledge RAG subsystem for the Quorums capstone (.NET 10 / ASP.NET Core, Postgres + pgvector). Use when building or modifying brand-knowledge corpus ingest, type-dispatched chunking, embedding with nomic-embed-text prefixes, the four-stage hybrid retrieval pipeline (query transform, dense + sparse Postgres full-text-search recall, union, cross-encoder rerank), the IRetrievalService / IEmbeddingProvider / IRerankProvider / IQueryTransformer interfaces, the KnowledgeDoc / KnowledgeChunk EF schema, or the adversarial RAG tests (isolation, prefix correctness, ungrounded degrade, ingest idempotency, stage toggles). All design decisions are frozen in DL-010, DL-016, DL-024, DL-025, DL-026; implement them, never re-decide them.
license: MIT
metadata:
  author: dev-jamal25
  version: 1.0.0
  project: Quorums (Autonomous Digital Marketing Agency capstone)
  composes-with: marketing-agency-architecture, agent-orchestration-graph, dotnet-engineering-standards
---

# Brand-Knowledge RAG

The per-brand, manager-editable knowledge subsystem that grounds generation in
the Quorums capstone. This skill is the implementation contract for a Claude Code
build session: it states every design decision as a **given** and points to the
canonical rationale. **Re-decide nothing here.** If a choice feels open, it is
not — it is frozen in a Decision Log and you have mis-read the contract.

## Source of truth (do not restate full rationale; cite these)

- `System_Architecture_Foundation.md` — **DL-010** (RAG/pgvector isolation surface),
  **DL-016** (embedding model + prefixes), **DL-024** (embed + rerank runtime: TEI),
  **DL-025** (four-stage retrieval pipeline), **DL-026** (corpus taxonomy + chunking),
  **DL-033** (`Retrieve` `docType` is a typed `DocType?`; stored `doc_type` is PascalCase,
  not the DL-026 snake_case taxonomy — detail in `references/interfaces-and-schema.md`).
- `Agent_Orchestration_Design.md` — **DL-017–023** (graph, contracts, failure +
  degradation; retrieval consumers and the ungrounded-degrade rule live here).

This skill composes with `marketing-agency-architecture`, `agent-orchestration-graph`,
and `dotnet-engineering-standards`. It must not contradict them.

## Scope — what this skill is and is NOT

**IN:** corpus modeling, the ingest pipeline, type-dispatched chunking, embedding,
the four-stage retrieval pipeline, the .NET interfaces, the EF schema + migration,
and the adversarial tests that prove all of it.

**OUT (belongs to other skills/phases — do not build here):**

- Per-agent **prompt architecture** and **structured-output schemas** → the
  `generation-pipeline` skill.
- The Phase-9 **golden set and evaluation metrics** → `evaluation-and-ci-gates`.
  This skill's only obligation to Phase 9 is to keep every pipeline stage
  **independently toggleable** so the ablation can run (see Invariants).
- **Hard platform rules** (hashtag caps, aspect ratios, compliance gates) → these
  are deterministic `PlatformConstraints` validators in the generation/publishing
  path, **not RAG**. `platform_guidance` corpus holds **soft** guidance only; never
  model a hard rule as a retrievable chunk.
- **Seed-corpus content** → a dev deliverable. One constraint applies: seed
  `historical_post`s must carry real `engagement_rate` / `ctr` values, or the
  reranker's performance blend has nothing to boost and the demo signal is dead.

## The frozen design (givens)

### Purpose and consumers
A per-brand knowledge base ingested into pgvector in the **same Postgres**,
retrieved to ground generation. **Retrieval-using agents are Content Strategist,
Creative Director, and Copywriting.** Publishing is **not** a retrieval consumer.
Every corpus row is brand-scoped under Postgres RLS — RAG **inherits** the existing
isolation mechanism (EF `DbConnectionInterceptor` + transaction-scoped
`set_config('app.current_brand', …, true)`). A manual `WHERE brand_id` is **never**
the isolation mechanism (DL-010, DL-002).

### Corpus taxonomy — five `docType`s
A `docType` discriminator drives **both** the chunker and the retrieval metadata
filter. Metadata is stored **structured, never embedded into chunk text.**

| docType | Chunk primitive | Key metadata | Retrieved by |
|---|---|---|---|
| `brand_playbook` | Section-aware window | `facet` = voice / persona / mission / visual_style | Creative Director (visual_style), Copywriting (voice), Strategist (mission, persona) |
| `historical_post` | **Whole-unit (never split)** | `engagement_rate`, `ctr`, `audience_segment`, `objective`, `date` | Strategist, Copywriting (provisions future Ads/Analytics) |
| `product` | Whole-unit (one product / one FAQ pair per chunk) | `product_id`, `price`, `category` | Strategist, Copywriting, Creative Director |
| `market_intel` | article → window; competitor copy → whole-unit | `source`, `date` (freshness), `is_competitor` | Strategist |
| `platform_guidance` | Whole-unit per heuristic | `platform`, `surface` = reel / feed / story | Strategist, Creative Director, Copywriting |

`platform_guidance` and `market_intel` are **per-brand** (maintained by that
tenant's manager role); there is **no global/shared corpus** in the MVP — banked as
a production optimization (DL-026).

### Chunking — one pipeline, type-dispatched, exactly two primitives
- **Whole-unit:** embed the unit as one chunk, never split; structured fields ride
  as metadata.
- **Section-aware window:** split on document structure (headings), then sliding
  window **~400–600 tokens, ~60 overlap** within sections.

Not five bespoke chunkers; not one blind chunker. The same `docType` that
dispatches the chunker pre-filters retrieval. Detail → `references/corpus-and-chunking.md`.

### Embedding + rerank runtime (DL-016 → superseded by DL-024)
- Embeddings: self-hosted **`nomic-embed-text-v1.5`**, **768-dim, normalized,
  cosine** (`vector_cosine_ops`), **HNSW** index.
- Reranker: **`bge-reranker-v2-m3`** cross-encoder.
- Both served from **HF Text-Embeddings-Inference (TEI)** — **two containers, same
  image, one `--model-id` each** (`tei-embed`, `tei-rerank`). TEI loads one model
  per process, so this is two containers. This replaces the Ollama embedding server
  **and** the `ollama-init` one-shot; **net service count is 9** (DL-024).
- **Mandatory prefixes:** `search_document:` on corpus chunks at ingest;
  `search_query:` on queries at retrieval. Missing/mismatched prefixes **silently
  degrade** retrieval — a guarded invariant (below), not a nicety.
- **pgvector column dim MUST equal model output dim (768).** Set it in the migration.

> The prompt's §3.4 phrasing ("service count stays 8") is superseded by DL-024
> ("8 → 9"). Encode **9**.

### Sparse retrieval — single store
Sparse is **Postgres native full-text search** (`tsvector` + GIN index on the chunk
text). **No second datastore.** Dense (pgvector) and sparse (FTS) live in the same
Postgres under the same RLS (DL-025).

### Retrieval pipeline — four toggleable stages (DL-025)
`IRetrievalService.Retrieve(query, brandId, DocType?, k)` is the **stable surface**;
the four stages are **internal** to `PgVectorRetrieval`. Each stage is
**config-gated and independently toggleable**, and must **never crash a run** when
disabled.

```
agent query
  ├─[S0] Query transformation (toggle)
  │        multi-query expansion via Haiku → {q1,q2,q3}     (default 3 variants)
  ├─[S1] Hybrid recall, per variant (toggle per arm)
  │        dense : nomic(search_query:) → pgvector cosine top-N   (default N≈20)
  │        sparse: Postgres FTS (tsvector / GIN)             → top-N
  │        + metadata filter (docType, brand via RLS) on BOTH arms
  │        → UNION of candidates                  (recall, NOT ranking)
  ├─[S2] Rerank + metadata blend (the ranking authority)
  │        bge-reranker(query, doc) → relevance; blend in PgVectorRetrieval:
  │          historical_post : α·rel + β·norm(performance) + γ·segment_match
  │          market_intel    : α·rel + δ·recency_decay
  │          others          : α·rel  (α = 1)
  │        → top-k                                (default k≈5)
  └─ grounding context → agent
```

- **Fusion = union-recall + reranker-as-fusion.** Dense and sparse are *recall*
  arms; the cross-encoder is the **single ranking authority**. **No score-weight
  tuning between BM25 and cosine** (incomparable scales). **RRF** (`Σ 1/(k+rank)`,
  k≈60) is the named fallback if a fused score is ever needed before reranking.
- The metadata **blend lives in `PgVectorRetrieval`**; `IRerankProvider` returns
  pure cross-encoder relevance. Keep that boundary clean.
- **Blend weights, N, k, variant count are config-bound, never hardcoded.**
- **S2 rerank defaults OFF (DL-055)** — the cross-encoder regresses rank-aware
  precision at per-tenant corpus scale (Phase-9 ablation); config-gated and
  re-enableable, revisit per the DL-055 trigger.

Stage-by-stage detail, blend math, config knobs → `references/retrieval-pipeline.md`.

### Degradation (DL-022)
RAG returns nothing → **proceed ungrounded, flag lower confidence** on the agent
output / `RunState`. **Never throw into the graph.** Tool failures return a
structured `ToolError`. This is the same degrade-don't-crash rule the orchestration
layer enforces.

### Ingest (DL-026)
Triggered by `KnowledgeController` CRUD. On create/update → enqueue a **Hangfire
job**: chunk → embed (`search_document:`) → **idempotent upsert keyed by chunk id**.
DELETE purges the doc's chunks. Re-ingesting a doc replaces its chunks (no
duplicates).

## Interface surface (the contract Claude Code implements)

| Interface | Implementation(s) | Notes |
|---|---|---|
| `IRetrievalService` | `PgVectorRetrieval` | Four-stage pipeline **internal**; `Retrieve(query, brandId, DocType?, k)` is the only public surface |
| `IEmbeddingProvider` | `NomicEmbeddingProvider` (HTTP → TEI) **+ CI mock** | Applies `search_document:` / `search_query:`; dim config-bound = pgvector dim |
| `IRerankProvider` | `CrossEncoderRerankProvider` (HTTP → TEI) **+ CI mock** | Returns **pure** cross-encoder relevance; no metadata blend here |
| `IQueryTransformer` | multi-query expander (Haiku) **+ CI mock** | S0; default 3 variants; config-gated |

Entities: **`KnowledgeDoc`** and **`KnowledgeChunk`** (the chunk carries the
**vector** column, a **`tsvector`** column, `docType`, optional `facet`, and a
**`metadata` JSON** column). Both brand-scoped, RLS-covered. Schema ships as an **EF
migration that includes the RLS policy + the HNSW and GIN indexes** and applies
cleanly from an empty volume. Interfaces, entities, and migration notes →
`references/interfaces-and-schema.md`.

All external/optional pieces (TEI embed, TEI rerank, sparse FTS, query transform)
are **config-gated** and never crash startup or a run.

## Invariants — verify every one before declaring RAG work done

1. **Isolation is inherited, never manual.** Brand scope comes from the RLS policy
   via the EF interceptor — on **both** the vector query **and** the FTS query.
   Never add a hand-written `WHERE brand_id` as the isolation mechanism.
2. **Prefixes are mandatory and matched.** `search_document:` at ingest,
   `search_query:` at retrieval. A mismatch silently degrades recall — it must be
   caught by a test, not by luck.
3. **pgvector column dim == model output dim (768).** Enforced in the migration;
   embedding dim is config-bound.
4. **Metadata is never embedded.** Structured fields live in the `metadata` JSON /
   typed columns and feed the filter + blend; they never enter the chunk text.
5. **Atomic content is never split.** `historical_post`, `product`/FAQ, competitor
   copy, `platform_guidance` heuristics are whole-unit.
6. **One ranking authority.** The cross-encoder ranks; dense+sparse only recall.
   No BM25/cosine score blending. RRF is the only sanctioned pre-rerank fusion, and
   only as a fallback.
7. **Blend stays in `PgVectorRetrieval`.** `IRerankProvider` is pure relevance.
8. **Every stage is config-gated and independently toggleable**, and disabling any
   stage never crashes a run. This is the precondition for the Phase-9 ablation.
9. **Soft vs. hard.** `platform_guidance` is soft guidance only; hard platform
   rules are `PlatformConstraints` validators elsewhere — never RAG.
10. **Ingest is idempotent.** Re-ingest replaces chunks (no dupes); DELETE purges.
11. **Degrade, don't crash.** Empty retrieval → ungrounded + confidence flag; tool
    failure → structured `ToolError`, never an exception into the graph.
12. **Tuning knobs are config-bound** (N, k, variants, blend weights) — not literals.

## Required adversarial tests (every slice ships its proof)

Specs in `references/tests.md`. At minimum:

1. **Isolation / leakage** — two seeded brands, **zero cross-brand leakage across
   both the vector query and the FTS query** (extends the existing
   `Category=Isolation` suite to RAG rows).
2. **Prefix correctness** — corpus embedded with `search_document:`, queries with
   `search_query:`; a mismatch is detected.
3. **Ungrounded degrade** — empty retrieval yields a grounded-flag-off result, not
   a crash.
4. **Ingest idempotency** — re-ingesting a doc replaces chunks with no duplicates;
   DELETE purges.
5. **Stage toggle smoke** — each pipeline stage independently on/off without
   breaking a run (the Phase-9 ablation precondition).

## Definition of done

A Claude Code session handed this skill + a build prompt implements the RAG
subsystem end-to-end **making none of the frozen decisions itself**, and the result
passes the five tests above with the four pipeline stages independently toggleable.
