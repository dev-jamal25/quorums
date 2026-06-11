# Control Flow and Human-Approval Gate

Encodes §5 (control-flow/handoff map), §6 and DL-021 (single human-approval gate).

## Control-flow / handoff map (§5)

```
ExecuteRun(runId) ── MAF graph ─────────────────────────────────────────┐
                                                                         │
 [Supervisor] load RunState, bind brand scope (IBrandContext → RLS)      │
      ▼                                                                  │
 [Content Strategist] ──RAG──▶ ContentStrategy                           │
      ▼                                                                  │
 [Creative Director]  ──RAG──▶ CreativeDirection                         │
      ▼                                                                  │
      ├───────────────── fork (parallel) ─────────────────┐             │
      ▼                                                    ▼             │
 [Copywriting] ──RAG──▶ Caption           [Media Gen] ──Gemini+MinIO──▶ MediaAsset
      └───────────────── join ────────────────────────────┘             │
      ▼                                                                  │
 [Supervisor] assemble ContentItem draft  +  BUDGET CHECK               │
      ▼                                                                  │
 ╔════════ HUMAN-APPROVAL INTERRUPT (DL-005, DL-021) ═══════╗           │
 ║ checkpoint RunState → RunCheckpoint (Postgres)           ║           │
 ║ AgentRun → awaiting_approval ; ExecuteRun job ENDS ──────╫───────────┘
 ╚══════════════════════════════════════════════════════════╝
                      ⌛ arbitrary human time
 POST /runs/{id}/approval ── reject ─▶ AgentRun → rejected (terminal)
                          └─ approve ─▶ enqueue ResumeRun(runId)

ResumeRun(runId) ── MAF graph (rehydrate from RunCheckpoint) ────────────┐
      ▼                                                                  │
 [Publishing] ──IMetaIntegration(mock)──▶ PublishResult                  │
      ▼                                                                  │
 [Supervisor] write EvalRecord refs ; AgentRun → done                    │
                                                                         │
 designed-for stubs, off MVP path: [Ads Optimization] [Analytics] ───────┘
```

### Sequencing rationale

- Creative Director consumes `ContentStrategy`, so Content Strategist → Creative
  Director is **sequential**.
- Copywriting and Media Generation both depend only on outputs available after
  Creative Director, so they **fork in parallel and join at assembly** — the one
  genuine concurrency win in the loop.

## Human-interrupt points (§6, DL-021)

**Single gate in the MVP:** on the assembled `ContentItem` (caption + media),
**immediately before publish** — matching DL-005 ("everything reversible is
autonomous; publish/paid needs a human"). Generation runs autonomously *before*
the gate; the human approves a **finished draft**, not a strategy.

### Gate mechanics (the frozen seam)

- Gate = checkpoint `RunState` → set `AgentRun = awaiting_approval` → **end the
  `ExecuteRun` job**.
- Approval: `POST /runs/{id}/approval` records the decision.
  - reject → `AgentRun = rejected` (terminal).
  - approve → enqueue `ResumeRun(runId)`.
- `ResumeRun` rehydrates `RunState` from `RunCheckpoint` and re-enters the graph
  at `GraphPhase` (here, Publishing).

### Deferred (do not add without a superseding entry)

A pre-generation strategy-approval gate was considered and **deferred** — it would
modify DL-005's "generation is autonomous" and is a deliberate scope change, not
a detail. The paid/ads gate is the same interrupt pattern, banked with the Ads
stub.

**Success signal:** no publish executes without a recorded approval; the gate
checkpoints and resumes correctly.
