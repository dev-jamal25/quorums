# Evaluators — rule-based & reference-based families

The third family (LLM-judge) is in `judge.md`. All evaluators implement `IEvaluator`
(`EvaluateAsync(case, output, ct) -> EvalResult{Score, Reasoning, Metadata}`).

---

## §1 — Rule-based family (tests · deterministic · merge-blocking)

Cheap, deterministic, run as **xUnit tests** in **mock mode** (`DeterministicGenerationChatClient`
+ `DeterministicMediaGenerationTool`, `Generation:ChatMode=mock`, `Gemini:Mode=mock`) — **zero API
spend**. These assert structure / contracts / trajectories, so they **block merges**. A forced tool
guarantees *shape, not truth* — so shape is checked here with rules, never with an LLM. These are **custom `IEvaluator` implementations** (the Microsoft.Extensions.AI.Evaluation interface) that make **no LLM call**; the library's LLM-based `ToolCallAccuracyEvaluator`/`IntentResolutionEvaluator`/`TaskAdherenceEvaluator` are an optional extra eval-tier signal, **not** a substitute for these deterministic invariants.

**Tool-call correctness (DL-028 / DL-030 / DL-034):**

1. **Schema validity** — each agent output deserializes cleanly into its canonical `Core/Orchestration/Contracts/`
   record (`ContentStrategy` with `candidates[3]`, `SelectionDecision`, `CreativeDirection` with a
   structured `MediaPromptBrief`, `Caption`), each carrying `Grounding { grounded, chunkIdsUsed[], confidence }`.
   The JSON schema is **derived from the C# record** (no hand-maintained dual). Score 1.0 if it
   deserializes and all required fields are present and typed; 0.0 otherwise.
2. **Bounded-retry trajectory** — a malformed output triggers **exactly 2 retries then `ToolError`**
   (not an exception into the graph). The test forces a schema violation and asserts the retry count
   and the terminal `ToolError`.
3. **`PlatformConstraints` (DL-030)** — `instagram_feed`: hashtagCount ≤ 30 and captionLength ≤ 2200
   (Copywriting), aspectRatio in {4:5, 1:1} (CD brief); `instagram_reel`/`instagram_story`: aspectRatio
   9:16. Remedy is per-constraint (hashtag over → repair; caption over → regenerate then truncate;
   aspect ratio → pre-enforced). Score on whether the **effective** output satisfies the constraint set.
4. **Selection-index range** — `SelectionDecision.chosenIndex` is within `[0, candidates.Count)`. Out
   of range → fail.
5. **`objective` / `pillar` validity** — `objective` is the fixed enum
   `{awareness|engagement|conversion|traffic|retention}`; `pillar` validates against the brand
   playbook's pillar list at receipt (a miss → regenerate, per DL-026).
6. **Grounding honesty (DL-034)** — `grounded` MUST equal `claimedChunkIds ∩ injectedChunkIds`. A
   caption claiming a chunk id that was **not** injected into its prompt is a faithfulness violation →
   fail. This is the deterministic faithfulness floor (claim-level faithfulness is a judge metric).

**Budget-degradation invariant (DL-023 / DL-034) — also a rule-based test:**

- Force a media-budget breach at the pre-Media gate. Assert: a **valid caption-only**
  `ContentItemDraft` is produced, a `BudgetDegraded` event is recorded in the trace, **zero Gemini
  calls** occur, and Copywriting is unaffected. Assert the global ceiling is a fork-time snapshot with
  at most a bounded single-call overshoot, and that `Budget` is written **only** by the Supervisor at
  the join (DL-020/034). Invariant phrasing: *never overspend, never crash, never fail silently.*

Each of the above is small and fully specified; a violation reds CI and blocks the merge.

---

## §2 — Reference-based family (evals · graded · tracked)

Ground-truth comparison against the hand-labeled golden set. **Metrics are chosen for a small
per-tenant corpus (DL-048)** — they measure *ordering*, which is what the hybrid + reranker change,
and they do **not** saturate the way recall@large-k does. Runs **local/mock, zero API spend**: S0
multi-query uses the `IQueryTransformer` Haiku **mock** (fixed variants); S1 (pgvector dense + Postgres
FTS sparse) and S2 (`IRerankProvider`, bge-reranker-v2-m3) are self-hosted.

These rank metrics are **custom `IEvaluator`s** — Microsoft.Extensions.AI.Evaluation has no deterministic retrieval-rank metric. Its `RetrievalEvaluator` (LLM-based) may run alongside as a complementary holistic retrieval-quality score, and the `.NLP` evaluators (BLEU/GLEU/F1) are available if a deterministic text-similarity metric is useful — but the ablation showpiece relies on the custom rank metrics below.

Notation: for a query `q`, `R_q` = set of golden-relevant chunk ids; the system returns a ranked list
`[d_1, d_2, …]`; `rel(d) = 1` if `d ∈ R_q`.

- **Context recall @ k** — `|{d_1..d_k} ∩ R_q| / |R_q|`. Report at **small k only (k = 1, 3)**.
  Meaningful at this corpus scale; recall@10 over ~13 docs is not — do not report it.
- **Hit@1** — `1` if `d_1 ∈ R_q`, else `0`. Mean over queries.
- **MRR** — mean of `1 / rank_of_first_relevant`. Rank-sensitive; exercises the reranker.
- **Context precision (rank-aware)** — for the top-k, average of precision@i taken at each rank `i`
  where `rel(d_i) = 1`, i.e. `Σ_i [ rel(d_i) · (relevant in d_1..d_i / i) ] / |R_q ∩ top-k|`. Rewards
  ranking relevant chunks **higher**; this is the primary stage-discriminating metric.
- **Faithfulness (deterministic floor)** — `grounded = claimedChunkIds ∩ injectedChunkIds` (DL-034).
  Reported as the fraction of generations with no unsupported chunk claim. (Claim-level faithfulness
  is in `judge.md`.)

**The four-stage ablation (DL-025).** Stages: **S0** multi-query → **S1** hybrid dense (pgvector,
cosine, 768-dim) + sparse (Postgres FTS over the `search_vector` tsvector) → **S2** cross-encoder
rerank. Each stage is **config-gated** (tuning knobs — N, k, variants, blend weights — are
config-bound, never hardcoded). Measure each stage's contribution by **paired per-query comparison**:
run stage-on vs stage-off on the **same** golden queries and report the per-query delta on context
precision / MRR.

**Statistical humility (DL-048 · deck S39) — mandatory in every reported result:**
- Always report **n** (number of golden queries).
- Treat deltas **< ~5 points** as noise; do not claim an improvement inside the noise band.
- Compare **paired per-case**, not aggregate-only (count queries where on > off vs off > on).
- At this corpus size, relevance labels may be **near-complete** (not sampled) — record that in the
  dataset `_meta`.

---

## §3 — Cost & latency (eval dimensions 3 & 4 · DL-049)

The deck makes cost and latency first-class eval dimensions ("2% more correct, 10× cost" is not a
win). These are **tracked evals**, read off the verified Langfuse generations (the durable trace
seam) plus the per-run cost model.

- **Per-node** tokens (in/out), cost (Langfuse auto-cost from model + tokens), and latency, for every
  graph node. The AsyncLocal run-trace context must be set at **both** `ExecuteRun` and
  `RegenerateRunJob` so no generation is orphaned.
- **Latency percentiles** — P50 / P95 / P99, computed in the harness over the per-case rows.
- **Cost source** — `Core/Generation/Cost/` (`TokenBudget` + `MediaBudget`); the dashboard budget
  panel is a frontend mock and is **not** the cost source — read the real numbers from the cost model
  + Langfuse.
- These are **tracked, not merge-blocking** — the only cost/latency *test* is the budget-degradation
  invariant in §1.

---

## Notes for the harness

- Rule-based evaluators are invoked as xUnit tests (`Category=Eval`); reference-based + cost/latency
  evaluators run via the `eval` CLI/runner and **persist** (see `datasets-ci-persistence.md`).
- All evaluators are **node-generic where possible** (keyed by node/contract), so activating the Ads
  Optimization / Analytics stub agents needs no harness change (DL-052).
- Brand-scope every retrieval/eval read with the transaction-scoped `set_config('app.current_brand', …)`
  pattern (first statement in an explicit work transaction; `NULLIF` on read).
