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

## Responses
- Never return secrets, tokens, or raw exception/stack detail. Errors are ProblemDetails.
- Health endpoint stays dependency-light (liveness, not a full integration probe).
