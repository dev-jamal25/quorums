# Topology and Framework

Encodes DL-017 (orchestration topology), DL-018 (framework), and §1/§9 of the
frozen design. Immutable. Do not re-decide.

## Topology — supervised deterministic graph (DL-017)

A fixed control spine with a Claude **Supervisor** making *bounded* decisions at
defined nodes. Specialized agents are nodes with distinct tools and typed
contracts — not autonomous peers exchanging free-form handoffs.

The spine:

```
onboard → strategy → creative direction → (copywriting ∥ media generation)
        → assembly → HUMAN GATE → publish (mock) → trace + eval
```

### Why supervised-graph and not a swarm — the four constraints a swarm fights

- **Reproducibility** — Phase 9 needs replayable, scoreable runs; emergent
  handoffs are nondeterministic by construction.
- **Human gate (DL-005)** — the gate must sit at a *known* node; a swarm has no
  clean pause point.
- **Cost ceilings** — a bounded graph caps the execution path; unbounded
  agent-to-agent handoffs are unbounded spend.
- **Defensibility** — "supervised graph because the workflow is mostly
  deterministic and the few real decision points are explicit" is the senior
  answer; "my agents emergently coordinate" invites an unwinnable interrogation.

The project plan's "murmuration / emergent-coordination" narrative maps, in code,
to a deterministic graph with bounded agent autonomy — stated honestly, never
claimed as literal emergence.

### Supervisor decision scope (bounded only)

The Supervisor makes only these bounded decisions: node sequencing,
proceed/regenerate, idea selection, budget checks, gate trigger, and failure
adjudication. It reasons over `RunState` with no external tool. It is the **sole
writer** of `Phase`, `Draft`, approval routing, and `Budget`.

## Framework — Microsoft Agent Framework 1.0 (DL-018)

MAF 1.0 (GA April 2026) is the orchestration framework. It is native, GA, LTS,
.NET-first, and MCP-capable (Anthropic is a first-class provider), and it maps
cleanly to the supervised graph. The `AgentRun` state machine owns the durable
pause/resume seam; MAF runs the graph within each `ExecuteRun` / `ResumeRun`
segment.

- MAF agents wrap the typed contracts in `runstate-and-contracts.md`.
- MCP (Meta) is wired through MAF's MCP integration.
- CI runs on mocks.
- Rejected alternatives: hand-rolled orchestration over the state machine
  (builds graph engine, retries, tracing by hand for no defensibility gain);
  Semantic Kernel (a predecessor MAF subsumes).

## MAF integration seam — the guardrail (§9, DL-018)

Microsoft Agent Framework executes the agent graph **only within** a single job
segment. The durable wait at the human gate is owned by the `AgentRun` state
machine:

- `ExecuteRun` runs the graph up to the gate, checkpoints `RunState` to
  `RunCheckpoint`, sets `AgentRun = awaiting_approval`, and exits.
- `ResumeRun` rehydrates `RunState` and continues from `GraphPhase`.
- MAF never holds the human wait in memory.

MAF owns *intra-segment* orchestration; the state machine owns the *durable*
pause/resume boundary. This is exactly the framework-agnostic fallback banked in
DL-015, made concrete.

### Risk to VALIDATE, not assume (DL-018)

Confirm MAF can checkpoint-and-exit cleanly at the gate. If its persistence
assumes in-process continuation, the state machine wraps it (DL-015 fallback) and
nothing is lost.

**Success signal:** a run checkpoints at the gate in `ExecuteRun` and resumes in
a fresh `ResumeRun` process with no in-memory continuation.
