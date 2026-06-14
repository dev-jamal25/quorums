# Open Questions and Deferrals

The architecture (`System_Architecture_Foundation.md`) is frozen for Phase 1. The
items below are **explicitly not decided there**. Do **not** invent decisions for
them. Where a default exists in the doc, it is stated; otherwise, stop and ask the
architect.

## Explicit deferrals (decided later, by design)

### 1. Phase 2 — agent orchestration internals (NOT FROZEN)

Deferred to Phase 2 and not decided in Phase 1:
- Orchestration **topology** (central supervisor / peer swarm / sequential / graph).
- **Agent roster** and responsibilities (Creative Director, Content Strategist,
  Media Generation, Copywriting, Publishing, Ads Optimization, Analytics).
- **Shared-state schema** (the typed object passed through the graph).
- **Tool assignment** per agent.
- **Orchestration framework choice.** The .NET switch makes **Microsoft Agent
  Framework / Semantic Kernel** the *native* front-runners (both .NET-first,
  MCP-capable, HITL patterns), but the choice is **not yet locked**. Any choice must
  satisfy banked constraints: (a) run as an async graph inside the Hangfire worker,
  (b) support a human-in-the-loop interrupt with durable Postgres checkpointing
  compatible with the enqueue–resume flow, (c) be MCP-capable for the Meta connector,
  (d) be defended on merits in review.

**Implication for the agent:** build the **Day-3 stub agent** now. Do **not**
implement the multi-agent graph until the Phase 2 output exists; Day 4 swaps it in.
The run-state machine, the two Hangfire jobs (`ExecuteRun`/`ResumeRun`), the
checkpoint/resume contract, and the human gate are framework-agnostic and fixed.

### 2. Phase 5 — generation/RAG tuning details

- **Chunking parameters** (size/overlap) — Phase 5.
- **Reranker runtime** — Phase 5 (the local-server pattern can host a cross-encoder
  via TEI if chosen then).
- **Golden retrieval sets** and prompt templates — Phase 5/9.

### 3. Phase 9 — evaluation specifics

- Concrete eval thresholds that gate CI, golden datasets, scoring methods — Phase 9
  consolidation. Day 7 builds the suite; the numbers come from the reserved golden
  examples.

## Documented defaults (use the default; switch only on instruction)

### A. .NET LTS version

Default **.NET 10 LTS**. If the employer pins **.NET 8 LTS**, it is a one-line
target-framework change with no architectural impact. Default to .NET 10; do not
switch unless told.

### B. Embedding output dimension

Default **768** (native). Truncatable to 512/256/128/64 (Matryoshka). Whatever is
chosen, the pgvector column dim **must equal** the `IEmbeddingProvider` output dim.
Default to 768 unless the architect specifies otherwise.

### C. Embedding model server runner

**Decided (DL-024/DL-025): HF Text Embeddings Inference.** Two containers:
`tei-embed` (`nomic-ai/nomic-embed-text-v1.5`) for embeddings and `tei-rerank`
(`BAAI/bge-reranker-v2-m3`) for cross-encoder reranking. Ollama was the prior
default (DL-016) and is superseded. Do not revert to Ollama.

## Flagged for re-pacing (not an architecture gap)

The Day-by-day schedule does **not** yet carve out dedicated React/Next.js
frontend-build time. This is flagged in the architecture for re-pacing on request.
Surface it to the architect rather than silently compressing other work.

## How to handle a genuine gap

If implementation requires a decision not covered by `System_Architecture_Foundation.md`,
`Product_Identity_and_Capstone_Scope.md`, or this skill:
1. Stop.
2. State precisely what is missing and why it blocks implementation.
3. Offer the trade-offs as information.
4. Wait for the architect's call. **Do not invent the decision.**
