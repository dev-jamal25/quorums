---
name: evaluation-suite
description: >-
  Implementation contract for the Quorums Phase-9 evaluation suite, built on the first-party
  Microsoft.Extensions.AI.Evaluation library. Use when building, extending, running, or debugging
  anything under eval/ or the eval harness: the IEvaluator protocol, rule-based
  tool-call-correctness checks, reference-based RAG/retrieval metrics (context precision, MRR,
  Hit@1, recall@k, faithfulness), the four-stage retrieval ablation, cost and latency tracking,
  the budget-degradation proof, the Gemini LLM-judge and its calibration, golden/adversarial
  dataset authoring and versioning, eval_run/eval_result persistence, and the merge-blocking CI
  gates and branch ruleset. Multi-tenant and scalable. Encodes DL-046 through DL-053; idioms are .NET.
metadata:
  project: quorums
  phase: "9"
  foundation: Microsoft.Extensions.AI.Evaluation
  decisions: DL-046..DL-052; adoption + revised boundaries in DL-053
  version: 1.1.0
---

# Quorums — Evaluation Suite (Phase 9)

This is the **Claude Code implementation contract** for evaluation, quality gates, and eval
scalability, derived from the frozen Decision Logs **DL-046 … DL-053**. The human-record rationale
lives in `System_Architecture_Foundation.md` (which Claude Code does **not** read) — **this skill is
the source of truth**. Do not re-derive or re-decide anything here; if a build surfaces a genuine
gap, **fix this skill in the same commit** that fixes the code.

## Foundation — built on Microsoft.Extensions.AI.Evaluation (DL-053)

The suite is built **on the first-party `Microsoft.Extensions.AI.Evaluation` library** — the eval
sibling of the Microsoft Agent Framework, on the same `Microsoft.Extensions.AI` `IChatClient` the
generator already uses. We do **not** hand-roll the evaluator abstraction, the response cache, or the
reporting.

- **Packages:** `Microsoft.Extensions.AI.Evaluation` (core — the **`IEvaluator`** interface we
  implement), `.Quality` (LLM-based built-in evaluators), `.NLP` (deterministic BLEU/GLEU/F1),
  `.Reporting` (response caching + results storage + reports), `.Console` (the `dotnet aieval` CLI
  for reports + managing cached responses/results). **Skip `.Safety`** (requires the Azure AI Foundry
  service — not in the stack, not a Phase-9 focus). **Pin a current stable 10.x** (aligned with .NET
  10 LTS); treat *experimental*-marked evaluators (e.g. `RelevanceTruthAndCompleteness`) with care.
  **`.NLP` is preview-only (no stable 10.x as of 10.6.0)** — it is **not** needed until the
  reference-based slice (slice 4), so add it there (pin whatever is stable then) rather than pinning a
  preview at the floor. The floor uses core + `.Quality` + `.Reporting` (all stable 10.6.0).
- **What the library gives us (so we don't build it):** the `IEvaluator` abstraction + pipeline;
  **response caching** (record real responses once, replay when prompt+model are unchanged = our
  no-spend CI); **reporting** (`dotnet aieval` HTML/pipeline report = the comparison table);
  **xUnit/`dotnet test`/CI** integration; and validated LLM-judge evaluators (Relevance, Groundedness,
  Coherence, Fluency, Completeness, Retrieval).
- **What stays custom** (our domain logic, on the library's `IEvaluator`): the **deterministic
  tool-call-correctness + budget-degradation** evaluators (no LLM); the **retrieval rank-metrics +
  the four-stage ablation**; the **brand-consistency** judge; and the **RLS-scoped Postgres reporting
  store** (the library persists only to disk or Azure Storage — there is no Postgres/RLS backend).

## Critical — read before writing any eval code

1. **Tests block, evals track (DL-046).** Deterministic checks (the rule-based family) are **tests**:
   fast, every commit, **merge-blocking**. Graded checks (built-in + custom reference/judge) are
   **evals**: tracked over time, **never** block a merge on noise.
2. **CI never spends on a live provider (DL-051).** Use the library's **response cache** (record once,
   replay) plus the deterministic clients (`DeterministicGenerationChatClient`,
   `DeterministicMediaGenerationTool`). Anthropic, Gemini, and Meta are all cached/mocked in CI. A live
   key in a CI run is a defect.
3. **Golden sets are hand-labeled, hold-out, never tuned against (DL-047).** Cases are human-labeled —
   never system-pre-labeled. Prompt iteration uses a **separate dev set**. The system is un-tuned now →
   the reserved set is clean; keep it that way.
4. **Everything is brand-scoped (DL-052).** Every dataset, metric, run, and persisted row is bound to a
   tenant with a **transaction-scoped** `set_config('app.current_brand', …, true)` issued as the
   **first statement inside an explicit work transaction** — not on connection-open. The RLS predicate
   reads `NULLIF(current_setting('app.current_brand', true), '')::uuid`.
5. **Never fail silently (DL-022/023).** Every eval error, fallback, and degradation is logged and
   surfaced.
6. **Small per-tenant corpora are the production norm (DL-048).** Use rank-sensitive retrieval metrics
   (context precision, MRR, Hit@1, recall@small-k); **never** recall@large-k (it saturates).

## What this skill governs

The eval subsystem only. It composes with — does not restate — the existing skills
(`generation-pipeline`, `brand-knowledge-rag`, `agent-orchestration-graph`,
`dotnet-engineering-standards`, `langfuse`). The contracts those own (typed agent outputs, retrieval
stages, the trace seam, the cost model) are the **inputs** this suite evaluates.

## Architecture (DL-046, boundaries revised by DL-053)

- **One interface — Microsoft's `IEvaluator`.** Built-in evaluators already implement it; our custom
  evaluators implement it too, so everything plugs into the same pipeline + reporting. We do **not**
  define our own evaluator interface.
- **Three families:**
  - **Rule-based (deterministic) → tests, merge-blocking.** Custom `IEvaluator`s, **no LLM**:
    tool-call correctness + the budget-degradation invariant. → `references/evaluators.md` §1.
    (Microsoft's LLM-based `ToolCallAccuracy`/`IntentResolution`/`TaskAdherence` are an optional extra
    eval-tier signal, not a substitute.)
  - **Reference-based (ground-truth) → evals, tracked.** Custom deterministic rank metrics (context
    precision, MRR, Hit@1, recall@small-k) + the four-stage ablation; the deterministic faithfulness
    floor. Microsoft's `RetrievalEvaluator` (LLM) may ride along as a complementary holistic score;
    `.NLP` (BLEU/GLEU/F1) is available if a text-similarity metric helps. → `references/evaluators.md` §2.
  - **LLM-as-judge (subjective, calibrated) → evals, top of pyramid, nightly/on-demand, never
    merge-blocking.** **Built-in Quality evaluators** (`Relevance` ≈ answer-relevance, `Groundedness`
    ≈ claim-faithfulness, `Coherence`/`Fluency`/`Completeness` ≈ caption-quality aspects) **with the
    judge `IChatClient` set to Gemini** (cross-family); a **custom** evaluator for brand consistency.
    → `references/judge.md`.
- **The eval pyramid:** many cheap rule-based checks at the base (CI, every commit), the reference-based
  middle (PR + nightly), a few expensive judge checks at the top (nightly/on-demand).

## Build order — slices (each = one adversarial proof + gate + commit on `feat/eval-dev`)

1. **Harness floor on the framework.** Add the M.E.AI.Evaluation packages (core, `.Quality`,
   `.Reporting`, `.Console`; **not** `.Safety`; **`.NLP` deferred to slice 4** — preview-only). Stand up the eval run via the library (its
   scenario/reporting config + response caching), following the dotnet/ai-samples API. Build the
   **custom RLS-scoped reporting store**: `eval_run` + `eval_result` EF entities + an EF migration with
   the RLS policy on **both** (brand-scoped), implemented as a custom result-store / cache provider on
   the library's reporting abstraction — **or** dual-write (library report to disk for the CI HTML,
   your Postgres for the multi-tenant queryable store). Wire the JSON dataset loader + `_meta` validator
   (`eval/datasets/<brandId>/<name>.json`) and commit a small fixture dataset at
   `eval/datasets/552732e7-0d74-4e58-9fdd-b6454479a38a/tool-call-fixture.json`. Detail:
   `references/datasets-ci-persistence.md`.
   **Adversarial proof:** a forced schema/constraint/grounding violation reds the (slice-2) rule-based
   test **and** the run still persists (RLS-scoped) with its git SHA + dataset version.
2. **Rule-based tool-call-correctness evaluators** (custom `IEvaluator`s, no LLM — the merge-blocking
   tests). Detail: `references/evaluators.md` §1.
3. **Golden + adversarial datasets.** Hand-label the per-brand golden retrieval set; reframe the existing
   proof tests as the adversarial set + add the corpus-poisoning case. Detail:
   `references/datasets-ci-persistence.md` §Datasets.
4. **Reference-based RAG eval** — custom rank metrics + the paired-per-query ablation toggling the DL-025
   stages (+ optional built-in `RetrievalEvaluator`). Detail: `references/evaluators.md` §2.
5. **Cost & latency eval** — per-node tokens/cost/latency off Langfuse + the budget-degradation
   invariant. Detail: `references/evaluators.md` §3.
6. **LLM-judge layer** — built-in Quality evaluators with the Gemini judge client + the custom
   brand-consistency evaluator + the κ ≥ 0.6 calibration harness. Detail: `references/judge.md`.
7. **CI gates + reporting wiring** — required status checks (admins-included), the `dotnet aieval`
   comparison report on PRs, hard-regression-only eval thresholds, cached/mocked providers, the nightly
   judge workflow. Detail: `references/datasets-ci-persistence.md` §CI.

Scalability (DL-052) is cross-cutting — bake brand-parameterization + node-generic evaluators into
slices 1/2/4/5; validate at the end with a "second brand runs with only its golden set added" check.

## Engineering standards + repo placement

- **Idioms are .NET (DL-015).** Async throughout; DI; typed boundaries; structured errors as
  `ToolError`/status codes, never `200`-with-error-body.
- **Packages:** `Microsoft.Extensions.AI.Evaluation`, `.Quality`, `.Reporting`, `.Console`
  (pin a current stable 10.x — 10.6.0; **not** `.Safety`). `.NLP` is preview-only → added in slice 4.
- **Placement:** custom evaluators (implementing the library's `IEvaluator`), the eval scenario/runner
  config, and the custom RLS reporting store live in `Core`/`Infrastructure` under `backend/src/*`. The
  **rule-based evaluators run as xUnit tests** under a new `Category=Eval` (alongside
  `Isolation`/`Storage`/`Durability`/`Generation`/`UnitTests`). Graded evals run via the library runner
  + `dotnet aieval`, **not** as merge-blocking tests.
- **Datasets:** versioned JSON in git under `eval/datasets/<brandId>/<name>.json`.
- **Persistence:** `eval_run` + `eval_result` are EF entities with an RLS-policy migration for each
  (brand-scoped), surfaced as a custom result store on the library's reporting abstraction.
- **Reporting/caching:** the `dotnet aieval` tool generates the comparison report; the library's
  response caching is enabled for no-spend CI.
- **Verification (CLAUDE.md checklist applies):** `dotnet build Backend.sln -warnaserror`;
  `dotnet format --verify-no-changes`; `dotnet test`; any data-access change → `dotnet test --filter
  Category=Isolation`; new migrations ship the RLS policy and apply cleanly from an empty volume;
  `gitleaks` clean.

## Gotchas (carry forward)

- **Framework version:** pin a current stable 10.x; the abstractions churned during the .NET 10 RC
  period but are settled on stable 10.x; treat *experimental*-marked evaluators with care.
- **No Postgres reporting backend out of the box** — the library persists to disk or Azure Storage; the
  RLS-Postgres store is our custom provider (or dual-write).
- **Built-in judges need a judge `IChatClient`** — set it to **Gemini** (cross-family); never the
  Claude generator (self-preference bias).
- **RLS bind:** transaction-scoped `set_config` as the **first** statement in an explicit work
  transaction; `NULLIF` on read. Superuser only for deliberate cross-tenant aggregation reads.
- **`doc_type` is stored PascalCase** — snake_case raw-SQL filters silently match nothing.
- **`IChatClient`** registered once via a **plain factory**, not `AddChatClient`/`.AsIChatClient()`.
- **Langfuse is optional/config-gated** — its absence never fails a run; `LocalTraceRecorder` fallback.
- **Mock/cache determinism** makes rule-based tests reproducible and spend-free; never let an eval path
  reach a live provider in CI.
