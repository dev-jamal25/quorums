# Decision Log — Orchestration (DL-017…DL-023)

Traceability and defensibility reference. Every behavior in this skill traces to
one of these frozen entries. Do not re-decide; supersede only via a new entry in
`Agent_Orchestration_Design.md`.

## DL-017 — Supervised deterministic graph

A fixed control-spine graph with a Claude Supervisor making bounded decisions at
defined nodes; agents are nodes with distinct tools and typed contracts, not
peers. Rejected: peer swarm (fights reproducibility, the gate, cost ceilings,
defensibility) and pure sequential pipeline (loses bounded routing + the parallel
fork).
**Defensibility:** "Supervised graph because the workflow is mostly deterministic
and the few real decision points are explicit — emergence I can't reproduce is an
eval liability."
**Success signal:** identical run replays to an equivalent trace; the gate sits at
one known node.

## DL-018 — Framework: Microsoft Agent Framework 1.0

MAF 1.0 (GA, LTS, .NET-first, MCP-capable) runs the graph within each segment;
the `AgentRun` state machine owns the durable pause/resume seam. Rejected:
hand-rolled orchestration; Semantic Kernel (subsumed by MAF).
**Defensibility:** "Employer mandates .NET; MAF is Microsoft's GA, LTS agent
framework with GA MCP support — the native, supported choice."
**Risk to validate:** confirm MAF can checkpoint-and-exit cleanly at the gate; if
not, the state machine wraps it (DL-015 fallback).
**Success signal:** a run checkpoints at the gate in `ExecuteRun` and resumes in a
fresh `ResumeRun` process with no in-memory continuation.

## DL-019 — Agent roster and granularity

Seven agents: five active (Content Strategist, Creative Director, Copywriting,
Media Generation, Publishing) plus the Supervisor; two (Ads Optimization,
Analytics) as designed-for stubs wired into the graph, not exercised in the MVP,
not cut.
**Defensibility:** "Each agent owns one responsibility with one tool surface; the
two I don't run in the MVP are stubbed, not invented or cut."
**Success signal:** the responsibility matrix shows no overlapping ownership; the
two planning agents write disjoint `RunState` slices.

## DL-020 — Shared state object and typed contracts

One typed `RunState` threaded through the graph and persisted as `RunCheckpoint`;
agents read/write declared slices only; handoffs are typed, never free-form text;
Supervisor is sole writer of `Phase`/`Draft`/`Budget`.
**Defensibility:** "The graph passes one typed, serializable state object — that's
what makes pause/resume durable and runs replayable."
**Success signal:** `RunState` round-trips through Postgres and rehydrates
identically in a fresh process.

## DL-021 — Single publish gate

One human-approval interrupt in the MVP, on the assembled `ContentItem`
immediately before publish; generation runs autonomously before the gate. A
pre-generation strategy gate was deferred.
**Defensibility:** "The human approves the finished, reversible-up-to-here draft
right before the one irreversible step — publishing."
**Success signal:** no publish executes without a recorded approval; the gate
checkpoints and resumes correctly.

## DL-022 — Two-layer failure isolation, idempotent side effects

Tool-layer failures return structured `ToolError` into the loop (no exceptions);
the Supervisor adjudicates retry/degrade/fail per node. Job-layer (whole-segment)
failures rely on Hangfire retries, made safe by checkpointing plus idempotent
side-effecting nodes (MinIO keyed by `assetId`, publish keyed by
`contentItemId`). Fatal nodes are Content Strategist and Creative Director.
**Defensibility:** "Tool failures degrade the run; process failures re-run from a
checkpoint — and every side effect is idempotent so re-runs are safe."
**Success signal:** kill the worker mid-segment after a checkpoint; the retried
segment completes without duplicate assets or double publishes.

## DL-023 — Cost ceiling and degradation policy

Per-run token and media budgets in `RunState`; the Supervisor checks budget
before the Media node. **Media-generation failure → retry-then-fail-item. Budget
breach at the Media node → skip media → caption-only draft.** Never overspend,
crash, or fail silently. Video carries its own higher ceiling; per-campaign
ceiling deferred.
**Defensibility:** "Cost is checked before the expensive node, and the run
degrades to caption-only rather than overspending or crashing."
**Success signal:** a forced budget breach yields a caption-only draft plus a
recorded degradation event in the trace.

## Inherited upstream anchors (do not contradict)

- DL-001 — Claude orchestrates; Gemini is a media-generation tool.
- DL-004 — Meta is mocked behind `IMetaIntegration`; live is a swap-in bonus.
- DL-005 — everything reversible is autonomous; publish/paid needs a human.
- DL-015 — .NET backend; Hangfire on Postgres; the state machine is the
  framework-agnostic durable seam.
- DL-016 — embeddings via self-hosted nomic-embed-text-v1.5; queries use the
  `search_query:` prefix.
