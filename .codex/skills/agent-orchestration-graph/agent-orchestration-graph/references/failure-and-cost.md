# Failure Isolation and Cost Ceiling

Encodes DL-022/§7 (failure isolation) and DL-023/§8 (cost ceiling and
degradation). Both are frozen policy.

## Failure isolation — two deliberately separated layers (§7, DL-022)

### Tool layer

Every external call returns `success | ToolError(code, message, retryable)`. **No
exceptions enter the graph.** The Supervisor adjudicates per node:

- retryable → bounded retry with backoff (Polly);
- non-retryable or exhausted → degrade or fail per node policy.

Per-node policy:

- **Degradable:** RAG returns nothing → proceed ungrounded, flag lower
  confidence.
- **Media Generation failure → retry-then-fail-item** (frozen policy, DL-023).
- **Fatal:** Content Strategist or Creative Director failure (no content without
  direction) → `AgentRun = failed`.
- **Publishing failure** (recorded `PublishResult`) → `AgentRun = failed`, trace
  preserved.

### Job layer

Hangfire's automatic retries cover a whole segment dying (process crash mid-
`ExecuteRun`). This is safe **only because** state is checkpointed — therefore
every side-effecting node **must use an idempotency key or check-before-write**,
since a retried segment re-runs from the last checkpoint.

- MinIO writes are keyed by `assetId`.
- Publish is keyed by `contentItemId`.

**Success signal:** kill the worker mid-segment after a checkpoint; the retried
segment completes without duplicate assets or double publishes.

## Cost ceiling and degradation (§8, DL-023)

`RunState.Budget` carries `TokenBudget/Spent` and `MediaBudget/Spent` (media is
the expensive outlier per frozen Phase 0; video carries its own higher ceiling).
The Supervisor checks budget **before** the Media node and before any heavy LLM
step.

Frozen policy:

- **Media-generation failure → retry-then-fail-item.**
- **Budget breach at the Media node → skip media → caption-only draft**, and
  record a budget-degradation event in the trace.
- Never overspend, never crash, never fail silently.

Scope boundaries:

- Per-run ceiling now.
- Video carries its own higher ceiling.
- Per-campaign ceiling is advanced scope, banked (not implemented now).

**Success signal:** a forced budget breach yields a caption-only draft plus a
recorded degradation event in the trace.
