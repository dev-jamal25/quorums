---
paths:
  - "backend/src/Worker/**/*.cs"
  - "backend/src/Infrastructure/Orchestration/**/*.cs"
---

# Orchestration & Durable Execution

<!-- Loads when Claude touches the Worker (Hangfire jobs) or the agent-graph code that
     actually lives in Infrastructure/Orchestration (the MAF executors + workflow factories). -->

## The state machine owns durability; the graph does not
- Durable state lives in Postgres (`RunState` / `RunCheckpoint`). The Microsoft Agent Framework graph runs ONLY within a single segment between checkpoints. Never hold a run open in memory across a human gate.
- State passes through Postgres, never through the Hangfire job payload. A job payload is `runId` + segment marker — nothing more.

## Run status transitions go through the guard
- Every `RunStatus` change goes through the central transition guard (`AgentRun.TransitionTo` → `RunStatusTransition`), which is the single source of truth for legal edges. Raw `run.Status = …` assignments are BANNED outside initial entity creation (the `Queued` start state on a new `AgentRun`). A new edge is added to `RunStatusTransition`, not invented at a call site; an illegal transition throws `InvalidRunStatusTransitionException`.

## Supervisor authority
- The Supervisor is the SOLE writer of `RunState.Phase`, `Draft`, and `Budget`. Each agent writes only its declared slice.
- Handoffs between agents are typed records, never free-form strings the next agent has to parse.
- A regenerate phase-rewind is a Supervisor write (it is the Supervisor that moves `Phase` backward). A human edit at the gate is NOT a `RunState` write — it is recorded on the `ApprovalAction` record; `RunState.Draft` stays byte-identical to the AI output.

## Claude calls go through `IChatClient`
- Agents and graph nodes obtain a `Microsoft.Extensions.AI` `IChatClient` by constructor injection and call Claude through it. NEVER reference `Anthropic.SDK` types, and never introduce a second Claude-call path (no bespoke `HttpClient`, no direct SDK calls). The client is registered once in Infrastructure (see `infrastructure.md`); the model id is config-bound, never a literal.

## Human gate: edits, reject, regenerate
- The gate has three decisions: `approve` (optional caption/hashtag edits, optional schedule), `reject` (terminal — no resume, phase unchanged), and `regenerate` (re-enters the graph). Cancel is a separate action on an already-scheduled run, not a gate decision.
- Edits change caption/hashtags ONLY. The image is never edited — a bad image is reject or regenerate. The edit overlay lives on `ApprovalAction` and is applied by the Publishing node on resume; it is never written to `RunState.Draft`.
- Regenerate re-enters the graph: `same-angle` rewinds to Creative (Creative Director → Media); `reselect-angle` rewinds to Supervisor selection over the already-banked N=3 strategy candidates (no new Strategist call). This is the ONLY `AwaitingApproval → Running` back-edge; the gate is re-entrant and each visit appends an `ApprovalAction` row.
- Regenerate is hard-bounded by a per-run count drawing from the SAME global cost ceiling as generation. When the count is exhausted, regenerate is unavailable (only approve / approve-with-edit / reject remain).

## Scheduled publishing
- Approval branches the resume dispatch: immediate → `BackgroundJob.Enqueue(ResumeRun)`; scheduled → `BackgroundJob.Schedule(ResumeRun, scheduledFor - now)`. The delayed job persists in the Postgres job store and survives worker restarts — durability holds across the wait, exactly like a checkpointed gate.
- `Scheduled` (approved, waiting) and `Cancelled` (terminal) are APPENDED `RunStatus` members — never renumber existing values (they are persisted). Cancel-before-fire calls Hangfire `Delete`; reschedule is cancel-then-reschedule (no new transition).

## Idempotency (Hangfire retries WILL happen)
- Every side-effecting segment is idempotent: dedupe on `assetId` (storage) and `(contentItemId, channel)` (publish, DL-055). A retried segment that already ran is a no-op, not a duplicate.
- The publish is TWO-STEP (create unit → poll → publish) and is NOT naturally idempotent: a crash between the publish step and recording the result, then a retry, would double-post. The pre-publish guard reads the persisted `PublishRecord` keyed `(contentItemId, channel)` and re-enters on its state — the `CreationId` is committed BEFORE publish, so a crash-and-retry re-publishes the **same** container/photo (Meta dedups) rather than double-posting; a finalized record (`ExternalRef` set) is skipped. (Checking only for an existing `externalRef` does NOT close the crash-after-publish-before-record window.) The publish is **channel-aware** and loops the content item's target channels (Instagram + Facebook Page), each an independent `(contentItemId, channel)` unit. The mock MUST model the two-step + both crash windows for both channels, or the idempotency test is theater.
- Nothing irreversible — paid action, publish — is enqueued without an approval record. The gate stops the run; approval is what resumes it.

## Failure & tracing
- Tool failures return `ToolError` into the graph. An exception must never escape a tool into the orchestrator.
- Publish failures are classified from a TYPED `PublishResult` status (`Published` / `TransientFailure` / `TerminalFailure`), never by sniffing exception types. Transient → bounded Hangfire automatic retry (3 attempts, exponential backoff), each re-entering the idempotency guard. Terminal → `ToolError` → run `Failed`, the reason surfaced to the reviewer (0 retries).
- Trace every segment and tool call to Langfuse, tagged with the run id.
- Audit records (`ApprovalAction`, `PublishRecord`) are durable business records, NOT telemetry: they are written straight to Postgres and are NEVER gated by Langfuse (see `persistence.md`). Tracing may be absent; the audit must always persist.
