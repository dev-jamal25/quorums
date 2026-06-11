# Data Model and API Surface

Governs the data-model ownership map and the initial API surface. Immutable input.

## Data-model ownership map

Rule: **every domain entity except `Brand` carries `brand_id` and an RLS policy.**
A table without one must be justified in its migration. Hangfire's tables are
infrastructure in their own schema and hold no brand data.

| Entity | Brand-scoped (RLS) | Lives in | Written by | Notes |
|--------|--------------------|----------|------------|-------|
| `Brand` | — (is the scope) | Postgres | api (BrandService) | Root tenant entity |
| `BrandProfile` | yes | Postgres | api | Onboarding output |
| `KnowledgeDoc` | yes | Postgres | api (knowledge CMS) | Manager-editable corpus (Week-8 CMS pattern) |
| `KnowledgeChunk` (+vec) | yes | Postgres/pgvector | api (ingest pipeline) | Embedded grounding; RLS covers vectors |
| `AgentRun` | yes | Postgres | api creates, worker advances | State machine: queued → running → awaiting_approval → publishing → done/failed |
| `RunCheckpoint` | yes | Postgres | worker | Durable graph state for pause/resume |
| `ContentItem` | yes | Postgres | worker | Caption + asset refs + status |
| `Asset` (metadata) | yes | Postgres | worker | Binary in MinIO `brands/{brand_id}/…` |
| `ApprovalAction` | yes | Postgres | api | Audit: who approved/rejected what, when |
| `BrandMetaConnection` | yes | Postgres | api | Token ciphertext + metadata (see isolation-and-secrets.md) |
| `EvalRecord` | yes | Postgres | worker / eval jobs | Consolidated in Phase 9 |
| *(Hangfire tables)* | n/a | Postgres | Hangfire | Job store (separate schema); not brand data |

## Initial API surface

```
POST   /brands                          create brand from brief
GET    /brands/{id}                     brand profile
POST   /brands/{id}/knowledge           add knowledge doc (CMS)
GET    /brands/{id}/knowledge           list docs
PUT    /brands/{id}/knowledge/{doc_id}  edit doc
DELETE /brands/{id}/knowledge/{doc_id}  remove doc
POST   /runs                            start agent run (202 + run_id)
GET    /runs/{id}                       run status + state
GET    /runs/{id}/trace                 full agent trace
POST   /runs/{id}/approval              approve | reject → enqueue resume
GET    /assets/{id}                     presigned MinIO URL
GET    /health                          liveness (+ dependency checks)
```

## Controller conventions

- ASP.NET Core controllers grouped by resource: `BrandsController`,
  `KnowledgeController`, `RunsController`, `ApprovalsController`, `AssetsController`,
  `HealthController`.
- **All actions async.**
- All bodies are DTOs validated by `[ApiController]` model binding + FluentValidation
  (invalid → automatic `400` ProblemDetails).
- Errors surface as correct status codes via **ProblemDetails**, never `200` with an
  error body.
- **Brand scope is bound per-request before any query runs** (via `IBrandContext` +
  the EF interceptor — see `isolation-and-secrets.md`).
- **Long work never executes in a controller** — controllers enqueue Hangfire jobs
  and return (`POST /runs` → `202`).

## DTOs and validators

DTOs live in `Api/Dtos/` with their FluentValidation validators. Validate all
external input at the boundary; use typed schemas in and typed results out; do not
free-parse where a typed DTO is possible.
