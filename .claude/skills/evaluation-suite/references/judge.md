# Evaluators — LLM-as-judge family (DL-050)

The subjective layer. Used only where there is **no clean ground truth**: caption quality, brand
consistency, generated-answer relevance, and claim-level faithfulness. Top of the eval pyramid —
**runs nightly / on-demand, persisted and tracked, NEVER merge-blocking** (cost + residual
subjectivity).

## Framework mapping (DL-053)

Most of this layer is **built-in Microsoft.Extensions.AI.Evaluation Quality evaluators**, not hand-written rubrics:

- **Generated-answer relevance → `RelevanceEvaluator`.**
- **Claim-level faithfulness → `GroundednessEvaluator`** (above the deterministic `grounded = claimed ∩ injected` floor).
- **Caption-quality aspects → `Coherence` / `Fluency` / `Completeness`.**
- **Brand consistency → a custom `IEvaluator`** (no built-in for brand voice) using the anchored-bucket rubric below.

All built-in quality evaluators take the judge **`IChatClient`** you give them — **set it to Gemini** (cross-family, to defeat self-preference). The calibration gate (κ ≥ 0.6) and bias mitigations below apply to the **built-ins on our data** as much as to the custom evaluator: if a built-in's rubric doesn't agree with human ratings on our domain, tune its prompt where configurable or fall back to a custom evaluator. The anchored-bucket rubrics below are the **brand-consistency rubric** and the **calibration reference** for the built-ins.

## Judge model — cross-family (Gemini)

- The judge is **Gemini**, a **different model family** than the Claude generator, to defeat
  self-preference bias (a Claude judge favors Claude outputs). This is a Gemini **text-completion**
  path — **distinct** from the existing `gemini-2.5-flash-image` media path.
- Behind an interface (e.g. `IJudgeClient`), **config-gated and mockable**. The model id is
  **config-bound, never hardcoded** (same rule as DL-025 tuning knobs). In CI the judge is a
  **mock or replays cached results** — **no live spend, ever** (DL-051).
- Pin the model + **temperature 0** so scores don't drift run-to-run.

## Rubric form — anchored buckets, not numeric scales

Every judge metric uses **3–5 anchored buckets** with concrete descriptions — **no 1–10 scales**
(they drift between runs). `EvalResult.Score` maps the bucket to `[0,1]`; `Reasoning` records the
judge's justification; `Metadata` records the bucket label + judge model/version.

**Caption quality** (anchored buckets):
- `excellent` (1.0) — on-brand voice, clear hook + CTA, correct for the `objective`, no platform/format issues.
- `good` (0.66) — on-brand and usable; weak hook **or** soft CTA.
- `weak` (0.33) — generic or off-objective; would need a rewrite before publishing.
- `unusable` (0.0) — off-brand, incoherent, or violates the brief.

**Brand consistency** (anchored buckets):
- `consistent` (1.0) — voice, terminology, and claims match the brand playbook + retrieved facts.
- `minor_drift` (0.66) — largely on-brand; one off-voice phrase or unsupported flourish.
- `off_brand` (0.33) — tone or claims diverge from the playbook.
- `contradicts` (0.0) — asserts something the brand facts contradict.

**Generated-answer relevance** (does the caption address the brief/strategy):
- `fully_addresses` (1.0) / `mostly` (0.66) / `tangential` (0.33) / `off_topic` (0.0).

**Claim-level faithfulness** (every factual claim supported by retrieved context — the judge layer
above the deterministic `grounded` floor):
- `fully_grounded` (1.0) — every claim traces to an injected chunk.
- `mostly` (0.66) — one minor unverifiable embellishment.
- `partly` (0.33) — a material claim with no support.
- `fabricated` (0.0) — invents facts not in context.

## Bias mitigations (deck S35) — required

- **Position bias** — for any **pairwise** judging (A vs B), randomize order and run **twice with
  positions swapped**; keep a verdict only if it survives the swap.
- **Verbosity bias** — the rubric explicitly states **"longer is not better"**, and a test checks the
  judge does not reward length (e.g. a padded variant must not outscore a tight correct one).
- **Self-preference** — handled by the cross-family Gemini judge (above).
- **Brittle rubrics** — anchored buckets + pinned model + temperature 0 (above).

## Calibration gate (deck S36) — a metric is not trusted until it passes

Before any judge metric is used in a report or a regression check:
1. Sample **20–30 mixed-difficulty cases** from the golden/dev set.
2. A human (Jamal) rates each on the **same** anchored-bucket rubric.
3. The judge rates the same cases.
4. Compute agreement: **Cohen's κ** and mean-absolute-error over the bucket→score mapping.
5. **Require κ ≥ 0.6** (and a bounded MAE). If below, **rewrite the rubric** and re-calibrate — do
   **not** ship an uncalibrated judge metric. An uncalibrated judge is *less* accurate, not more.

Persist the calibration run (cases, human ratings, judge ratings, κ, MAE) alongside eval runs so the
metric's trustworthiness is auditable.

## Placement

- Judge metrics run via the `eval` CLI on a **nightly / on-demand** schedule, persist to
  `eval_run`/`eval_result`, and appear in the tracked comparison report — **not** on the merge gate
  (DL-051).
- `eval_result` for a judge metric stores: score, bucket label, `Reasoning`, judge model + version,
  and (for pairwise) the swap outcome.
