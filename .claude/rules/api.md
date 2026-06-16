---
paths:
  - "backend/src/Api/**/*.cs"
---

# API Layer

<!-- Loads when Claude touches the Api project. The boundary: validate, delegate, map. -->

## Controllers stay thin
- Flow is: validate input → call a `Core` service → map to a response DTO. No `DbContext`, no business logic, no agent-graph execution in a controller.

## Validation boundary
- Every request body is a DTO with a FluentValidation validator. Rely on `[ApiController]` for automatic 400 ProblemDetails — don't hand-roll validation responses.
- `CancellationToken` flows from the action through to I/O. Async all the way down.

## Run lifecycle endpoints
- Trigger endpoints ENQUEUE a Hangfire job and return `202 Accepted` + run id. They NEVER run the agent graph inline on the request thread.
- Approval / publish endpoints are the human gate: confirm the run is at an `AwaitingApproval` checkpoint before enqueuing `ResumeRun`. No path auto-approves or auto-publishes.
- The gate is two endpoints: `POST /runs/{id}/approval` carries a decision-discriminated body (`approve` | `reject` | `regenerate`); `POST /runs/{id}/cancel` is separate and acts ONLY on a `Scheduled` run (409 Conflict otherwise).
- `approve` may carry caption/hashtag edits and an optional `scheduledFor`. The edit DTO is validated by the SAME `PlatformConstraints` rule used at publish (caption at most 2200, hashtags at most 30) → 400 on violation. `scheduledFor`, when present, must be strictly in the future (UTC).
- `reject` is terminal (no resume). `regenerate` re-enters the graph and is rejected if the per-run regenerate ceiling is already reached. `approve` requires `AwaitingApproval`; `cancel` requires `Scheduled`.
- The review GET returns a SERVER-COMPUTED DTO that includes the list of currently-legal actions; the client never recomputes gate policy.

## Responses
- Never return secrets, tokens, or raw exception/stack detail. Errors are ProblemDetails. The review DTO exposes a grounding summary (grounded flag, chunk ids, confidence) — never raw chunk text or token material.
- Health endpoints follow the liveness/readiness split:
  - `/health/live` is liveness-only — cheap, dependency-free, safe for orchestrator restart probes.
  - `/health/ready` performs full dependency checks (postgres, redis, minio, vault, embeddings)
    and is used by readiness probes and the local-dev gate.
  - `/health` aliases `/health/ready` for convenience.
  - Health responses never include exception text, stack traces, or secret material — only
    `{status, description, durationMs}` per dependency.
