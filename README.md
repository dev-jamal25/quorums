# Quorums

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![C#](https://img.shields.io/badge/C%23-239120?logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)
[![ASP.NET Core](https://img.shields.io/badge/ASP.NET%20Core-512BD4?logo=dotnet&logoColor=white)](https://learn.microsoft.com/aspnet/core/)
[![Next.js](https://img.shields.io/badge/Next.js%2015-000000?logo=nextdotjs&logoColor=white)](https://nextjs.org/)
[![TypeScript](https://img.shields.io/badge/TypeScript-3178C6?logo=typescript&logoColor=white)](https://www.typescriptlang.org/)
[![PostgreSQL + pgvector](https://img.shields.io/badge/PostgreSQL-pgvector-4169E1?logo=postgresql&logoColor=white)](https://github.com/pgvector/pgvector)
[![Docker Compose](https://img.shields.io/badge/Docker-Compose-2496ED?logo=docker&logoColor=white)](https://docs.docker.com/compose/)
[![Built with Claude Code](https://img.shields.io/badge/Built%20with-Claude%20Code-DA7857?logo=anthropic)](https://claude.ai/code)

> Claude-supervised multi-agent system that turns one brand's brief into on-brand
> Instagram content (image + caption) through a deterministic, durable agent graph.

## Overview

Quorums is a production-shaped backend that automates digital-marketing content
generation for direct-to-consumer brands. A **Claude-orchestrated agent graph**
(Microsoft Agent Framework) takes a brand brief through Content Strategy → Creative
Direction → Copywriting → Media Generation, grounded in a per-brand knowledge base
via hybrid RAG. **Gemini** generates media behind a swappable tool interface; **Meta**
publishing sits behind a mocked boundary so the full loop runs end-to-end with zero
live API access.

Two invariants shape the whole design. **Nothing publishes or spends without a human
approval gate** — runs pause as durable jobs and resume only on approval. And
**multi-brand isolation is structural**, enforced by Postgres Row-Level Security,
MinIO key prefixes, and per-brand Vault Transit token encryption rather than by
hand-written `WHERE` clauses.

This is a capstone built to demonstrate AI-engineering ability that is defensible
line-by-line in review — not a shipped product.

## Architecture

**Nine services in one `docker-compose.yml`:** `api`, `worker`, `frontend`,
`postgres` (pgvector), `redis`, `minio`, `vault`, `tei-embed`, `tei-rerank`.

The backend is a single `Backend.sln` with four layered projects:

| Project | Role |
|---|---|
| `Api` | ASP.NET Core HTTP surface — brands, knowledge CMS, runs, approvals, assets |
| `Worker` | Worker Service hosting Hangfire — runs durable jobs (`ExecuteRun` / `ResumeRun`) |
| `Core` | Domain, agent contracts, cost model, boundary interfaces |
| `Infrastructure` | EF Core/Npgsql, Vault, MinIO, integrations (Claude/Gemini/Meta), retrieval |

`Api` and `Worker` ship from one publish output, so there is zero API↔worker skew.
Runs are **durable jobs, not HTTP requests**: Hangfire (Postgres job store) checkpoints
`RunState` at the approval gate and resumes through Postgres — never through job
payloads. Redis is `IDistributedCache` only, never a queue broker. Retrieval is a
four-stage hybrid pipeline (dense pgvector + sparse Postgres FTS → union → cross-encoder
rerank) over self-hosted `nomic-embed-text-v1.5` embeddings served by HF TEI.

## Tech Stack

- **Backend:** .NET 10, C#, ASP.NET Core, Hangfire, EF Core + Npgsql
- **AI / Agents:** Claude (Anthropic API + .NET MCP SDK), Microsoft Agent Framework 1.0, Gemini, Langfuse tracing
- **Data:** PostgreSQL 16 + pgvector (RLS isolation, embeddings, checkpoints, Hangfire store)
- **Infra:** Docker Compose, Redis, MinIO, HashiCorp Vault (KV + Transit), HF Text Embeddings Inference
- **Frontend:** Next.js 15, React 19, TypeScript
- **Quality:** FluentValidation, Polly, xUnit + Testcontainers, gitleaks, GitHub Actions CI

## Getting Started

### Prerequisites

- Docker + Docker Compose (the demo target)
- [.NET 10 SDK](https://dotnet.microsoft.com/) (only for local builds, tests, and migrations)

### Run locally

```bash
git clone git@github.com:dev-jamal25/quorums.git
cd quorums
cp .env.example .env   # fill in values — no secrets are committed
docker compose up
```

On a fresh volume, `tei-embed` and `tei-rerank` download model weights on first
start (allow 2–5 minutes before their health checks pass and `api`/`worker` start);
weights are cached in named volumes for fast subsequent runs. Once up:

- API → `http://localhost:8080`
- Frontend → `http://localhost:3000`
- MinIO console → `http://localhost:9001`

### Apply migrations on a fresh volume

`docker compose up` does **not** auto-migrate. After a clean start (or `down -v`),
from `backend/`:

```bash
dotnet ef database update -p src/Infrastructure -s src/Api \
  --connection "Host=localhost;Port=5432;Database=quorums;Username=postgres;Password=postgres"
```

### Build and test

```bash
cd backend
dotnet build Backend.sln -warnaserror   # nullable + analyzer warnings are errors
dotnet format --verify-no-changes       # style gate
dotnet test                             # xUnit unit + Testcontainers integration suite
dotnet test --filter Category=Isolation # two-brand RLS leakage check
```

## Configuration

Every config key and environment variable is documented **by name only** (no values)
in [`appsettings.Example.json`](appsettings.Example.json) and
[`.env.example`](.env.example). Required keys fail fast at startup via the Options
pattern. Secrets load from Vault (KV → Options; Transit → per-brand token encryption);
nothing sensitive lives in source, images, the database, or logs.

Note the **`host:port` convention**: `*__Endpoint` / `*__Address` keys store
`host:port` only — the app prepends `http://` at registration. Live Meta and Vault
are config-gated and optional; their absence never fails a run or `/health`.

## API Surface

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/brands` | Onboard a brand |
| `POST` | `/knowledge` | Add a brand-knowledge document |
| `PUT`/`DELETE` | `/knowledge/{id}` | Update / remove a knowledge document |
| `POST` | `/runs` | Start a content-generation run |
| `GET` | `/runs` · `/runs/{id}` | List runs / get run status |
| `GET` | `/runs/{id}/review` · `/media` | Fetch the draft for approval / generated media |
| `GET` | `/runs/{id}/trace` | Run trace (per-node + per-tool spans) |
| `POST` | `/runs/{id}/approval` | Approve, reject, or edit a draft (the human gate) |
| `POST` | `/runs/{id}/cancel` | Cancel a run |

All inputs are validated by DTOs + FluentValidation; errors surface as status-code
ProblemDetails, never `200` with an error body.

## Repository Structure

```
quorums/
├── backend/                  # Backend.sln
│   ├── src/
│   │   ├── Api/              # ASP.NET Core controllers, DTOs, health checks
│   │   ├── Worker/          # Hangfire worker host
│   │   ├── Core/            # Domain, agent contracts, cost model, interfaces
│   │   └── Infrastructure/  # EF Core, Vault, MinIO, integrations, retrieval
│   └── tests/               # UnitTests + IntegrationTests (Testcontainers)
├── frontend/                 # Next.js dashboard (onboarding, approvals, run/trace viewer)
├── eval/                     # Evaluation datasets + report
├── .github/workflows/        # CI (build/test on mocks) + nightly eval
├── docker-compose.yml        # Nine-service demo topology
├── appsettings.Example.json  # Every config key by name (no values)
└── .env.example              # Every env var by name (no values)
```

## Testing & CI

CI ([.github/workflows/ci.yml](.github/workflows/ci.yml)) runs on every PR and
feature-branch push, **entirely on mocks** — no live Meta, Gemini, embeddings, or
Anthropic keys. It gates on format, build (Roslyn analyzers, warnings-as-errors),
and the full xUnit suite, with integration tests spinning their own Postgres via
Testcontainers. A nightly workflow runs the evaluation suite. Install the local
pre-commit hooks (`gitleaks` + `dotnet format`) once per clone to mirror the gates:

```bash
pip install pre-commit && pre-commit install
```

## Roadmap

- ✅ Durable run pipeline with human approval gate (checkpoint / resume)
- ✅ Structural multi-brand isolation (RLS + storage prefixes + Transit)
- ✅ Generation agents (Content Strategist, Creative Director, Copywriting, Media)
- ✅ Hybrid RAG retrieval + evaluation suite
- 🔜 Ads Optimization and Analytics agents (currently typed stubs)
- 🔜 Live Meta publishing (present-but-throwing seam behind `IMetaIntegration`)
- 🔜 Video generation (Veo) and dual-channel (Instagram + Facebook) publishing

## Reporting Issues

Found a bug or have a suggestion? Open an issue at
[github.com/dev-jamal25/quorums/issues](https://github.com/dev-jamal25/quorums/issues).
Please include steps to reproduce, expected vs. actual behavior, and environment details.

## Contact

**Jamal Hamd**

- GitHub: [@dev-jamal25](https://github.com/dev-jamal25)
- LinkedIn: [linkedin.com/in/jamal-hamd](https://www.linkedin.com/in/jamal-hamd/)
