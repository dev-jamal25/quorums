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

<!-- TODO: first-run notes (migrations, MinIO bucket, Ollama model pull, Vault init). -->

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
