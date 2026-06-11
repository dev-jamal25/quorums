# Run Execution Model (DL-006)

Governs: queue + worker + durable checkpoint/resume, the human-approval gate, and
the end-to-end demo data flow. Immutable input.

## Core model

- Agent runs execute as **background jobs in the `worker`**, not in an HTTP
  request. Controllers enqueue and return immediately.
- The run is a **persisted state machine in Postgres** (`AgentRun`), not an
  in-memory object.
- Queue technology is **Hangfire on PostgreSQL** (DL-015 supersedes Arq). Hangfire's
  job store is a **separate Postgres schema** holding no brand data.
- Exactly two jobs:
  - `ExecuteRun(runId)`
  - `ResumeRun(runId)`
- **All state passes through Postgres, never through job payloads.** Jobs carry only
  the `runId`.
- Hangfire's automatic retries cover transient job failures.

## AgentRun state machine

```
queued → running → awaiting_approval → publishing → done
                              │
                              └──────────────────────────▶ failed  (from any step)
```

`AgentRun.Status` (a C# enum) is the **single source of run truth**. The columns
that back pause/resume are the `RunCheckpoint` (durable graph state) plus the
`AgentRun` row.

## End-to-end data flow (the demo path)

1. **Onboard** — dashboard → `POST /brands` with the brand brief → brand profile
   persisted (brand-scoped). Knowledge docs added via CMS endpoints → chunked +
   embedded into pgvector (brand-scoped rows).
2. **Start run** — `POST /runs` → API creates `AgentRun(status=queued)` → enqueues
   `ExecuteRun(runId)` on Hangfire → returns **`202`** immediately with `run_id`.
3. **Execute** — the worker's Hangfire server picks up the job and runs the
   orchestration graph (Claude-orchestrated, DL-001). Agents retrieve brand
   grounding from pgvector; the Media agent calls Gemini through
   `IMediaGenerationTool`; generated assets go to MinIO under the brand prefix;
   asset metadata to Postgres.
4. **Gate** — the graph hits the human-approval interrupt (DL-005). Full run state
   **checkpoints to Postgres**; `AgentRun → awaiting_approval`; the job completes.
   Nothing is held in memory; nothing waits on a connection.
5. **Approve** — the approval dashboard shows the draft (image + caption + trace).
   `POST /runs/{id}/approval` records the decision → enqueues `ResumeRun(runId)`.
6. **Publish** — the worker resumes from the checkpoint → calls `IMetaIntegration`
   (mock per DL-004) → records the publish result → `AgentRun → done`.
7. **Trace + eval** — the run trace is viewable in the dashboard; eval records are
   written against the run (consolidated in Phase 9).

Failure at any step → structured error on the run, `AgentRun → failed`, trace
preserved. A **tool** failure returns a structured `ToolError` into the loop
(degrade, don't crash) rather than throwing.

## Controller contract

- `POST /runs` does **only**: create `AgentRun(queued)`, enqueue `ExecuteRun`,
  return `202` + `run_id`. No agent logic, no long work in the controller.
- `POST /runs/{id}/approval` records the `ApprovalAction` and enqueues `ResumeRun`.
- All controller actions are async; bodies are DTOs validated by `[ApiController]`
  binding + FluentValidation; errors surface via ProblemDetails with correct status
  codes, never `200` with an error body.

## Human-approval gate (DL-005)

- Autonomous through ideation, generation, and draft scheduling.
- **Human-gated at any `publish` action and any paid/ads action.**
- No publish or paid action executes without an explicit `ApprovalAction` recorded
  in the loop.

## Success signal (must demonstrate)

Kill the worker mid-run **after** the checkpoint; approve; resume completes the run
with **no data loss**. This proves durability of the queue + checkpoint design.

## Phase 2 boundary

The **internal** orchestration graph (topology, agent roster, shared-state schema,
tool assignment, framework choice) is **deferred to Phase 2 and not frozen here**.
Day 3 uses one stub agent; Day 4 replaces it with the Phase 2 graph. The run-state
machine, the two jobs, the checkpoint/resume contract, and the human gate are
framework-agnostic and fixed. See `open-questions.md`.
