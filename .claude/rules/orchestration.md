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

## Supervisor authority
- The Supervisor is the SOLE writer of `RunState.Phase`, `Draft`, and `Budget`. Each agent writes only its declared slice.
- Handoffs between agents are typed records, never free-form strings the next agent has to parse.

## Claude calls go through `IChatClient`
- Agents and graph nodes obtain a `Microsoft.Extensions.AI` `IChatClient` by constructor injection and call Claude through it. NEVER reference `Anthropic.SDK` types, and never introduce a second Claude-call path (no bespoke `HttpClient`, no direct SDK calls). The client is registered once in Infrastructure (see `infrastructure.md`); the model id is config-bound, never a literal.

## Idempotency (Hangfire retries WILL happen)
- Every side-effecting segment is idempotent: dedupe on `assetId` (storage) and `contentItemId` (publish). A retried segment that already ran is a no-op, not a duplicate.
- Nothing irreversible — paid action, publish — is enqueued without an approval record. The gate stops the run; approval is what resumes it.

## Failure & tracing
- Tool failures return `ToolError` into the graph. An exception must never escape a tool into the orchestrator.
- Trace every segment and tool call to Langfuse, tagged with the run id.
