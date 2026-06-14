# Adversarial proof tests

Every RAG slice ships its proof. These are the **minimum** required specs (DL-025 /
DL-026 success signals, DL-022 degradation, DL-010 isolation). They run on the CI
mocks for the providers and on Testcontainers Postgres for the schema/RLS behavior —
no live model server in CI. Idioms (xUnit, `WebApplicationFactory`, Testcontainers)
follow `dotnet-engineering-standards`.

## 1. Isolation / leakage — `[Trait("Category","Isolation")]`

The existing two-brand isolation suite, **extended to RAG rows on both retrieval
arms.**

- **Arrange:** seed two brands (A, B), each with corpus chunks containing
  brand-distinctive terms; embed + populate `tsvector` for both.
- **Act:** bind brand A's scope (via `IBrandContext` → interceptor → RLS), then run
  retrieval that would match B's content on **both** the **vector (dense) query**
  and the **FTS (sparse) query**.
- **Assert:** **zero** B chunks returned on either arm. Repeat bound to B → zero A
  chunks. Neither arm leaks.
- **Guards:** that isolation is the RLS policy, not a manual `WHERE brand_id`; that
  the sparse arm is covered too (a common gap — FTS queries are easy to forget when
  the policy was only mentally applied to the vector side).

## 2. Prefix correctness

- **Arrange:** ingest corpus through the real prefix path (`search_document:`) using
  the embedding mock that **records the prefix it received**.
- **Act:** issue a retrieval; capture the prefix used on the query embed.
- **Assert:** corpus embeds carried `search_document:`; the query embed carried
  `search_query:`. A test that swaps them (or drops one) **fails** — proving a
  mismatch is detected, not silently tolerated.
- **Why:** mismatched/missing prefixes degrade recall *silently* with
  `nomic-embed-text-v1.5`; only a test catches it before review does.

## 3. Ungrounded degrade

- **Arrange:** a brand with **no** matching corpus (or all stages configured to
  return empty recall).
- **Act:** call `Retrieve(...)`.
- **Assert:** result has `grounded == false`, **no exception thrown**, and the agent
  path can proceed ungrounded with a lowered-confidence flag recorded on the
  output / `RunState`. A provider outage in this test returns a structured
  `ToolError`, never an exception into the graph.

## 4. Ingest idempotency

- **Arrange:** ingest a doc → record its chunk count and ids.
- **Act:** re-ingest the **same** doc (edit + update path → Hangfire job).
- **Assert:** chunks are **replaced**, not appended — same ids upserted, **no
  duplicates**, count stable. Then **DELETE** the doc and assert all its chunks are
  **purged** (zero remain).
- **Why:** the upsert is keyed by chunk id; this proves the key actually
  de-duplicates and that delete cascades to chunks.

## 5. Stage toggle smoke (Phase-9 ablation precondition)

For **each** independently-gated unit — S0 (query transform), S1 dense arm, S1
sparse arm, S2 rerank — run retrieval with that unit **off** and assert:

- the call **completes without crashing**,
- it returns a sane (possibly smaller / differently-ordered) result set,
- no exception enters the graph.

Also assert the all-off-but-one and one-on configurations the Phase-9 ablation will
use are runnable. This is the contract that lets Phase 9 measure each technique's
marginal contribution by flipping a config flag rather than editing code.

## Coverage map (test → frozen requirement)

| Test | Proves | Source |
|---|---|---|
| 1 Isolation | RLS covers RAG on both arms; no manual `WHERE` | DL-010, DL-002 |
| 2 Prefix | `search_document:` / `search_query:` correct + mismatch caught | DL-016 |
| 3 Degrade | empty recall → ungrounded flag, no crash; tool failure → `ToolError` | DL-022 |
| 4 Idempotency | re-ingest replaces (no dupes); delete purges | DL-026 |
| 5 Toggle smoke | every stage independently on/off, run never breaks | DL-025 (Phase-9) |

A slice that compiles but skips test 1 or test 5 is **not done** — those two are the
isolation guarantee and the ablation precondition the whole subsystem exists to
support.
