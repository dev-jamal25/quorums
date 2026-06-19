# Retrieval pipeline

Depth for the four-stage retrieval pipeline. Frozen by **DL-025** (pipeline,
fusion, sparse store) with degradation per **DL-022**. The public surface is
`IRetrievalService.Retrieve(query, brandId, docType?, k)`; everything below is
**internal to `PgVectorRetrieval`** and must not leak into the interface.

## 0. The one public method

```csharp
Task<RetrievalResult> Retrieve(
    string query,
    Guid   brandId,        // bound to RLS; not a manual WHERE
    string? docType,       // pre-filter; null = all types the caller may read
    int     k);            // final top-k (default ≈ 5)
```

`RetrievalResult` carries the ranked chunks **and** a `grounded` flag (false when
recall was empty — see §5). The four stages are hidden behind this call.

## 1. S0 — Query transformation (toggle)

Multi-query expansion via **Haiku** through `IQueryTransformer`: one input query →
**N variants** (default 3, config-bound). Expanding the query improves recall on
short or ambiguous agent queries before any retrieval happens.

- Config-gated: when off, the pipeline runs on the single original query. **Off must
  not crash** — it simply means one query arm instead of N.
- `IQueryTransformer` has a **CI mock** (deterministic variants) so the ablation and
  CI never call Haiku.

## 2. S1 — Hybrid recall (toggle per arm)

Run **per query variant**, two recall arms, both metadata-filtered, then unioned:

```
for each variant q:
    dense  = pgvector cosine top-N over embed(search_query: q)     # semantic
    sparse = Postgres FTS (tsvector / GIN) top-N over q            # lexical
    apply metadata filter (docType, + brand via RLS) to BOTH arms
candidates = UNION(all dense, all sparse)        # recall set, NOT ranked
```

- **Dense** captures semantic intent; **sparse** captures exact brand/product
  terminology that embeddings blur (SKU names, brand-specific jargon). You need
  both — that is the whole point of hybrid recall.
- **Sparse is Postgres native full-text search** — `tsvector` column + GIN index on
  the chunk text. **No second search engine, no second datastore.** Dense and sparse
  both live in the same Postgres under the same RLS.
- The **metadata filter applies to both arms**, and `brand` scoping is the RLS
  policy (via the EF interceptor), **never** a hand-written `WHERE brand_id`. This is
  the line the isolation test guards on both arms.
- `N` (per-arm recall depth, default ≈ 20) is config-bound.
- Each arm is independently toggleable. Dense-only or sparse-only must both run
  without crashing — that is part of the ablation.
- **This stage is recall, not ranking.** Do not sort or threshold the union here;
  ordering is S2's job.

## 3. S2 — Rerank + metadata blend (the ranking authority)

The union of candidates goes to the cross-encoder, then a per-`docType` metadata
blend produces the final order:

```
rel = bge-reranker-v2-m3(query, doc)         # via IRerankProvider — PURE relevance
score = blend(rel, chunk.metadata, docType)  # computed in PgVectorRetrieval
        historical_post : α·rel + β·norm(performance) + γ·segment_match
        market_intel    : α·rel + δ·recency_decay
        others          : α·rel               (α = 1)
return top-k by score
```

- **The cross-encoder is the single ranking authority.** It scores
  `(query, document)` pairs jointly — far stronger than comparing independent
  embedding distances.
- **The blend lives in `PgVectorRetrieval`, not in the provider.** `IRerankProvider`
  returns **pure** cross-encoder relevance and nothing else. Keep that boundary
  clean — it is what lets Phase 9 measure the reranker in isolation from the blend.
- **Blend weights (α, β, γ, δ) are config-bound, never hardcoded.** `historical_post`
  blends in normalized performance + audience-segment match (surfacing what worked
  for the right audience); `market_intel` blends in recency decay (fresher intel
  wins); everything else is pure relevance (α = 1).
- `k` (final cut, default ≈ 5) is config-bound.
- **Rerank defaults OFF at per-tenant corpus scale (DL-056).** The bge cross-encoder
  regresses rank-aware precision on small corpora (Phase-9 ablation: −0.094 from the
  cross-encoder itself, blend net-neutral), so `RerankEnabled` defaults off; revisit
  per the DL-056 trigger (a larger corpus). The stage stays config-gated and
  re-enableable — the ablation arms and demo comparison turn it on by config.

## 4. Fusion — union-recall + reranker-as-fusion

The fusion strategy is deliberately **not** a weighted score blend between arms:

- Dense (cosine) and sparse (BM25-style FTS rank) produce scores on **incomparable
  scales**. Tuning a weight between them is brittle and unmeasurable. **Do not do
  it.**
- Instead: the two arms **union** into one recall set, and the **cross-encoder is the
  single fusion + ranking authority** over that set. One scale, one authority.
- **RRF** (`Σ 1/(k + rank)`, k ≈ 60) is the **named fallback** — use it only if a
  fused score is ever genuinely needed *before* reranking. It is rank-based, so it
  sidesteps the incomparable-scale problem. It is not the default path.

## 5. Degradation (DL-022)

- **Empty recall → `grounded = false`, proceed ungrounded.** The retrieval call
  returns a result with the flag off and no chunks; the agent generates without
  grounding and the Supervisor records lower confidence on the output / `RunState`.
- **Never throw into the graph.** A provider failure (TEI down, timeout) returns a
  structured `ToolError(code, message, retryable)` — the same degrade-don't-crash
  contract the orchestration layer enforces. The Supervisor adjudicates retry vs.
  degrade.
- A disabled stage is **not** a failure — it is a configured path that must run
  cleanly (see §6).

## 6. Config-gating and toggleability (Phase-9 ablation precondition)

Every stage and every recall arm reads its on/off state and its tuning knobs from
config:

| Knob | Default | Stage |
|---|---|---|
| query-transform on/off, variant count | on, 3 | S0 |
| dense arm on/off | on | S1 |
| sparse arm on/off | on | S1 |
| recall depth `N` | ≈ 20 | S1 |
| rerank on/off | **off** (DL-056 — regresses at per-tenant corpus scale; re-enableable) | S2 |
| blend weights α, β, γ, δ | config | S2 |
| final `k` | ≈ 5 | S2 |

Hard requirement: **disabling any single stage or arm must leave a runnable
pipeline** — no crash, no exception into the graph. This is exactly what Phase 9
flips to measure each technique's marginal contribution, and what the stage-toggle
smoke test (`references/tests.md` §5) proves. None of these values may be a literal
in code.
