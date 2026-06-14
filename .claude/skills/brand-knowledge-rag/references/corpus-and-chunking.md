# Corpus and chunking

Depth for the corpus model and the ingest-time chunker. Frozen by **DL-026**
(taxonomy + type-dispatched chunking) and **DL-016 / DL-024** (embedding runtime).
Re-decide nothing; this file is the implementation detail behind the SKILL.md
taxonomy table.

## 1. The `docType` discriminator is load-bearing

A single enum drives three things, and they must stay in lock-step:

1. **Which chunk primitive** the ingest pipeline dispatches to.
2. **Which structured metadata** the chunk carries.
3. **Which retrieval metadata filter** an agent applies (the same `docType` that
   chunked the doc pre-filters recall in S1).

If you add a `docType`, you touch the chunker dispatch, the metadata shape, and the
retrieval filter together. There is no path where one drifts from the others.

## 2. The five document types

### `brand_playbook` — section-aware window
Long-form prose: brand voice, personas, mission, visual style. Split on headings,
then sliding window within each section.

- **Primitive:** section-aware window (~400–600 tokens, ~60 overlap).
- **Metadata:** `facet` ∈ { `voice`, `persona`, `mission`, `visual_style` }. The
  facet is what lets each agent pull only its slice — Copywriting filters
  `facet = voice`, Creative Director `facet = visual_style`, Strategist
  `mission` / `persona`.
- **Retrieved by:** Creative Director (visual_style), Copywriting (voice),
  Strategist (mission, persona).

### `historical_post` — whole-unit, never split
A past post is an atomic performance datum. Splitting it destroys the unit that the
reranker's performance blend scores.

- **Primitive:** whole-unit.
- **Metadata:** `engagement_rate`, `ctr`, `audience_segment`, `objective`, `date`.
- **Retrieved by:** Strategist, Copywriting. Provisions future Ads/Analytics agents
  (they will read the same rows).
- **Seed constraint:** performance fields must be **real, non-null** values — the
  S2 blend has nothing to boost otherwise (the demo's "reranker surfaces what
  worked" signal dies).

### `product` — whole-unit per product / per FAQ pair
One product record, or one FAQ question+answer pair, per chunk.

- **Primitive:** whole-unit.
- **Metadata:** `product_id`, `price`, `category`.
- **Retrieved by:** Strategist, Copywriting, Creative Director.

### `market_intel` — sub-type dispatch
Two shapes under one `docType`:
- **article** → section-aware window (it is prose).
- **competitor copy** → whole-unit (a competitor caption is atomic, like a post).

- **Metadata:** `source`, `date` (drives the S2 recency decay), `is_competitor`.
- **Retrieved by:** Strategist only.

### `platform_guidance` — whole-unit per heuristic
One soft heuristic per chunk (e.g. "reels under 15s retain better for this brand").

- **Primitive:** whole-unit.
- **Metadata:** `platform`, `surface` ∈ { `reel`, `feed`, `story` }.
- **Retrieved by:** Strategist, Creative Director, Copywriting.
- **HARD BOUNDARY:** this corpus holds **soft** guidance only. Hashtag caps, aspect
  ratios, and compliance gates are **hard** rules — deterministic
  `PlatformConstraints` validators in the generation/publishing path, **not** RAG.
  A hard rule must never depend on retrieval recall. Do not let one leak in here.

## 3. Per-brand, no global corpus (MVP)

`platform_guidance` and `market_intel` are **per-brand**, maintained by that
tenant's manager role. There is **no global/shared corpus** in the MVP. A shared
corpus is banked as a production optimization (DL-026) — do not build it now, and
do not assume any row is visible across brands. Every row is RLS-scoped.

## 4. The chunker — one pipeline, type-dispatched, two primitives

```
KnowledgeDoc (docType, raw content, structured fields)
        │
        ▼
   dispatch on docType
        │
        ├── whole-unit ────────────► one KnowledgeChunk, content = the unit
        │     (historical_post, product/FAQ, competitor copy,
        │      platform_guidance heuristic)
        │
        └── section-aware window ──► split on headings → sliding window
              (brand_playbook, market_intel article)     ~400–600 tok / ~60 overlap
                                                          → N KnowledgeChunks
```

Rules:
- **Whole-unit:** the chunk text is the unit, embedded as one vector. Never split,
  never windowed. Structured fields go to metadata, **not** into the text.
- **Section-aware window:** detect structure (headings) first so a window never
  straddles two unrelated sections, then slide within the section. Overlap preserves
  cross-boundary context.
- This is **not** five bespoke chunkers and **not** one blind splitter. Exactly two
  primitives, dispatched by `docType`.

## 5. Metadata: structured, never embedded

Every structured field (performance, recency, segment, facet, platform, price, …)
is stored in the typed columns / `metadata` JSON on `KnowledgeChunk`. **None of it
is concatenated into the chunk text before embedding.** Reasons:

- Embedding a number (`engagement_rate = 0.071`) produces noise, not signal — the
  embedding model has no notion of its magnitude.
- The S1 metadata filter and the S2 blend read these fields **structurally**; they
  need typed columns / JSON, not text the model may or may not have encoded.
- Keeping text clean keeps the `tsvector` (sparse arm) clean too.

The chunk text is the human-meaningful content; everything else rides alongside.

## 6. Embedding at ingest

- Model: self-hosted `nomic-embed-text-v1.5` via TEI (`tei-embed`), 768-dim,
  normalized, cosine.
- **Prefix every corpus chunk with `search_document:` before embedding.** This is
  the ingest half of the prefix invariant; the retrieval half (`search_query:`)
  lives in `references/retrieval-pipeline.md`. A mismatch silently degrades recall.
- Embedding dim is config-bound and **must equal** the pgvector column dim (768).

## 7. Ingest job shape (idempotent)

```
KnowledgeController CRUD
   ├─ create / update → enqueue Hangfire job:
   │     chunk(docType) → embed(search_document:) → UPSERT chunks keyed by chunk id
   │        (re-ingest replaces this doc's chunks — no duplicates)
   └─ delete → purge all chunks for the doc
```

- Upsert is **keyed by chunk id**, so a re-ingest of the same doc overwrites rather
  than appends. This is what the idempotency test (`references/tests.md` §4) proves.
- The job runs on the worker, under the brand-scoped DbContext — ingest writes are
  RLS-covered exactly like reads.
