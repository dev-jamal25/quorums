# Quorums

> Skeleton README — sections only. Fill in as the vertical slice lands.

## Overview

Autonomous multi-agent system generating on-brand Instagram content (images +
captions) for DTC brands. Claude orchestrates a supervised agent graph via
Microsoft Agent Framework; Gemini generates media behind a swappable tool
interface; Meta publishing is mocked behind a swappable boundary so the full loop
runs with zero live Meta access.

<!-- TODO: expand "what it does and why". -->

## Architecture

Eight services in one `docker-compose.yml`: `api`, `worker`, `frontend`,
`postgres` (pgvector), `redis`, `minio`, `vault`, `embeddings`. The backend is one
`Backend.sln` with four layered projects — `Api`, `Worker`, `Core`,
`Infrastructure` — where `Api` and `Worker` both reference `Core` + `Infrastructure`
and ship from one publish output (zero API↔worker skew). The queue/job store is
Hangfire on Postgres; Redis is `IDistributedCache` only. Three-layer brand
isolation: Postgres RLS (data), MinIO key prefixes (storage), Vault Transit (per-
brand Meta tokens).

<!-- TODO: architecture diagram (source: marketing-agency-architecture skill). -->

## Run locally

```bash
cp .env.example .env   # fill in values (no secrets are committed)
docker compose up
```

### Local dev setup — embeddings and reranker (DL-024/DL-025)

Two HF Text Embeddings Inference containers (`tei-embed` and `tei-rerank`) are
started automatically by `docker compose up`. They download model weights on first
start — allow up to 2–5 minutes on a fresh volume before the health checks pass
and `api`/`worker` start.

**Model weights are cached in named Docker volumes** (`tei-embed-cache`,
`tei-rerank-cache`) so subsequent `docker compose up` runs start in seconds.

**Endpoints (host port mapping for local curl):**

```bash
# embedding health
curl http://localhost:8090/health

# reranker health
curl http://localhost:8091/health

# embed a string (768-dim vector)
curl -s http://localhost:8090/embed \
  -H 'Content-Type: application/json' \
  -d '{"inputs": "search_query: test"}'

# rerank a query+texts
curl -s http://localhost:8091/rerank \
  -H 'Content-Type: application/json' \
  -d '{"query": "marketing strategy", "texts": ["brand guidelines", "budget plan"]}'
```

Config keys (`host:port` only — app prepends `http://`):

```
Embeddings__Endpoint=tei-embed:80
Reranker__Endpoint=tei-rerank:80
```

<!-- TODO: first-run notes (migrations, MinIO bucket, Vault init). -->

## Pre-commit hooks

Install the hooks once per clone to run **gitleaks** and **`dotnet format`** on
staged changes before each commit (mirrors the CI gates). Requires the .NET SDK
on `PATH` for the format hook.

```bash
pip install pre-commit   # or: pipx install pre-commit / brew install pre-commit
pre-commit install
```

The same checks run in CI ([.github/workflows/ci.yml](.github/workflows/ci.yml)).

## Configuration

All config keys are documented by name (no values) in
[`appsettings.Example.json`](appsettings.Example.json) and
[`.env.example`](.env.example). Required keys fail fast at startup via the Options
pattern. Secrets load from Vault (KV → Options; Transit → per-brand token crypto);
nothing sensitive lives in source, images, the DB, or logs.

<!-- TODO: list the required keys and where each is sourced. -->

## Repository layout

```
backend/        # Backend.sln — Api, Worker, Core, Infrastructure (+ tests); one Dockerfile
frontend/       # Next.js (React, TypeScript) dashboard shell; Dockerfile
docker-compose.yml
appsettings.Example.json   # every config key by name (no values)
.env.example               # every env var by name (no values)
```

<!-- TODO: pointers to the interesting code (RLS interceptor, boundary interfaces,
     agent graph) once implemented. -->
