---
name: agent-orchestration-graph
description: Implements the autonomous digital-marketing-agency multi-agent orchestration layer as a supervised deterministic graph on Microsoft Agent Framework 1.0 with a .NET backend. Use when building or wiring the agent graph, the Supervisor and the seven agents (Content Strategist, Creative Director, Copywriting, Media Generation, Publishing, plus Ads Optimization and Analytics stubs), the RunState shared-state object, typed inter-agent contracts, the single pre-publish human-approval gate with checkpoint and ResumeRun, two-layer failure isolation, cost-ceiling degradation, or Langfuse tracing. Trigger phrases an agent will hit include "implement the orchestration graph", "wire up the agents", "add the human approval gate", "build the Supervisor node", "set up RunState", "MAF agent graph", "agent failure isolation", "ExecuteRun ResumeRun", "media generation tool node".
---

# Agent Orchestration Graph

This skill encodes the FROZEN Phase-2 orchestration design for the autonomous
digital marketing agency. It is the implementation contract for the agent layer.
Every decision here is already locked in the Decision Log (DL-017…DL-023). Do not
re-decide, omit, or invent anything. When an implementation choice feels open,
it is not — it is specified in a reference file below.

## CRITICAL CONSTRAINTS — read before writing any orchestration code

1. **Topology is a supervised deterministic graph, never a swarm.** A fixed
   control spine; a Claude Supervisor makes only *bounded* decisions at defined
   nodes. Agents are graph nodes with distinct tools and typed contracts — not
   autonomous peers exchanging free-form handoffs. (DL-017)
2. **MAF runs the graph only *inside* a single job segment.** Microsoft Agent
   Framework 1.0 owns intra-segment orchestration. The durable human wait is
   owned by the `AgentRun` state machine, NOT by MAF. `ExecuteRun` checkpoints
   and exits at the gate; `ResumeRun` rehydrates and continues from
   `GraphPhase`. MAF must never hold the human wait in memory. (DL-018, §9)
3. **`RunState` is the only shared state.** One typed object threaded through the
   graph and persisted as `RunCheckpoint` (JSON). Agents read/write ONLY their
   declared slice. The Supervisor is the **sole writer** of `Phase`, `Draft`,
   approval routing, and `Budget`. No free-form blackboard. (DL-020)
4. **Exactly one human-approval gate**, on the assembled `ContentItem`
   immediately before publish. Generation runs autonomously before it.
   Gate = checkpoint `RunState` → `AgentRun = awaiting_approval` → end the
   `ExecuteRun` job. Approval enqueues a separate `ResumeRun`. (DL-021)
5. **Tools return structured errors, never exceptions into the graph.** Every
   external call returns `success | ToolError(code, message, retryable)`. The
   Supervisor adjudicates retry/degrade/fail per node. (DL-022, §7)
6. **Every side-effecting node must be idempotent.** Hangfire can re-run a whole
   segment after a crash; that is safe only because state is checkpointed.
   MinIO writes are keyed by `assetId`; publish is keyed by `contentItemId`.
   Use an idempotency key or check-before-write. (DL-022)
7. **Cost is checked before the expensive node.** The Supervisor checks
   `Budget` before the Media node and before any heavy LLM step. Media-generation
   failure → retry-then-fail-item. Budget breach at the Media node → skip media →
   caption-only draft + a recorded degradation event. Never overspend, never
   crash, never fail silently. (DL-023, §8)
8. **Brand scope is bound before any query.** The Supervisor binds
   `IBrandContext` → RLS at graph entry. Retrieval agents call
   `IRetrievalService` under the RLS-bound DbContext and embed queries with the
   `search_query:` prefix (DL-016).

## What this skill builds

The orchestration layer that runs as the body of the Hangfire jobs `ExecuteRun`
and `ResumeRun`: a Claude-supervised MAF graph of seven agents producing one
on-brand Instagram `ContentItem` (image + caption) per run, human-gated before a
mocked publish, fully traced. Backend is .NET (DL-015); Claude orchestrates
(DL-001); Gemini is a media-generation *tool* (DL-001); Meta is mocked behind
`IMetaIntegration` (DL-004).

## The spine (fixed control flow)

```
onboard → strategy → creative direction → (copywriting ∥ media generation)
        → assembly → HUMAN GATE → publish (mock) → trace + eval
```

Sequencing is fixed: Strategist → Creative Director is sequential (the Creative
Director consumes `ContentStrategy`). Copywriting and Media Generation fork in
parallel after Creative Director and join at assembly — the one genuine
concurrency win. Full diagram in `references/control-flow-and-gate.md`.

## Build order (do not break the seam)

1. Define `RunState` and the six typed agent-output records exactly as specified
   in `references/runstate-and-contracts.md`. These are the contracts; build them
   first.
2. Implement `GraphPhase` as the single resume marker and the `AgentRun` state
   machine (`queued → running → awaiting_approval → publishing → done/failed`,
   plus `rejected` terminal).
3. Wire the MAF graph nodes per `references/agent-roster.md`, registering each
   agent with its declared tools only. Stubs (Ads Optimization, Analytics) are
   wired into the graph wiring and return a not-implemented marker — present, not
   exercised, not cut.
4. Implement the Supervisor as sole writer of `Phase`/`Draft`/`Budget` with the
   bounded decisions in `references/agent-roster.md` §Supervisor.
5. Implement the gate as checkpoint-and-exit; implement `ResumeRun` rehydration
   from `RunCheckpoint`. See `references/control-flow-and-gate.md`.
6. Implement two-layer failure isolation and idempotent side effects per
   `references/failure-and-cost.md`.
7. Implement budget checks and degradation per `references/failure-and-cost.md`.
8. Add Langfuse spans per node and per tool call per
   `references/tracing-and-eval.md`.

## MAF seam — the one risk to validate, not assume

MAF 1.0 runs the graph within each segment; the state machine owns the durable
pause/resume. **Validate that MAF can checkpoint-and-exit cleanly at the gate.**
If MAF's persistence assumes in-process continuation, the `AgentRun` state
machine wraps it (the DL-015 fallback) — checkpoint `RunState`, end the segment,
re-enter from `GraphPhase` on resume. Nothing is lost either way. Details in
`references/topology-and-framework.md`.

## Reference files (load as needed)

- `references/topology-and-framework.md` — DL-017 topology rationale (why not a
  swarm) and DL-018/§9 MAF integration seam.
- `references/agent-roster.md` — DL-019 seven-agent responsibility matrix
  (owns / tools / consumes / produces), granularity defenses, stub rules.
- `references/runstate-and-contracts.md` — DL-020 `RunState` record (§3) and the
  typed inter-agent contracts (§4).
- `references/control-flow-and-gate.md` — §5 control-flow/handoff map and §6/
  DL-021 single human-approval gate and resume flow.
- `references/failure-and-cost.md` — DL-022/§7 two-layer failure isolation and
  idempotency, plus DL-023/§8 cost ceiling and degradation policy.
- `references/tracing-and-eval.md` — §10 Langfuse tracing and replayability.
- `references/decision-log.md` — DL-017…DL-023 defensibility one-liners and
  success signals, for review and self-check.

## Common implementation errors — and the required fix

- **Holding the human wait in memory / inside MAF.** Wrong. Checkpoint and end
  the job; resume is a fresh process re-entering at `GraphPhase`. (Constraint 2)
- **Letting an agent write outside its slice, or two agents writing the same
  field.** Wrong. The responsibility matrix has no overlapping ownership; the two
  planning agents write disjoint `RunState` slices. (Constraint 3)
- **Throwing from a tool into the graph.** Wrong. Return `ToolError`; the
  Supervisor decides retry/degrade/fail. (Constraint 5)
- **Re-running a segment that double-writes an asset or double-publishes.**
  Wrong. Key MinIO by `assetId`, publish by `contentItemId`. (Constraint 6)
- **Generating media without a pre-check on `Budget`.** Wrong. Check before the
  Media node; degrade to caption-only on breach. (Constraint 7)
- **Cutting the Ads/Analytics stubs to save time.** Wrong. They are required
  advanced scope wired in as designed-for stubs; the MVP five-agent path is the
  deliverable floor, the stubs stay. (DL-019)

## Validation before you call it done

Confirm every item, then stop:

- Spine matches the fixed order; gate sits at exactly one known node.
- `RunState` round-trips through Postgres and rehydrates identically in a fresh
  process; an identical run replays to an equivalent trace.
- Supervisor is the only writer of `Phase`/`Draft`/`Budget`; no overlapping
  ownership in the matrix.
- Killing the worker mid-segment after a checkpoint, then resuming, completes the
  run with no duplicate assets and no double publish.
- A forced budget breach yields a caption-only draft plus a recorded degradation
  event in the trace.
- No publish executes without a recorded approval.
- Langfuse spans exist per agent node and per tool call.
