# Stack and Topology

Governs: DL-015 (.NET stack), DL-016 (embeddings container), the eight-service
Docker Compose. Immutable input.

## Backend stack (DL-015)

- **Runtime:** .NET 10 LTS. ASP.NET Core Web API (`api`) + .NET Worker Service
  (`worker`).
- **Solution shape:** one `Backend.sln`, four layered projects:
  - `Api` — ASP.NET Core Web API (HTTP surface, controllers, DTOs, DI, Hangfire
    client).
  - `Worker` — Worker Service hosting the Hangfire server (job execution).
  - `Core` — domain: entities, value objects, interfaces, agent contracts.
  - `Infrastructure` — EF Core DbContext + Migrations (incl. RLS SQL), Vault,
    MinIO, Meta/Media integrations, retrieval, embeddings.
- `Api` and `Worker` both reference `Core` + `Infrastructure` and are built from
  **one publish output** → zero version skew between API and worker.

### .NET version note (documented in the architecture)

`.NET 10` is the current LTS and the default target. **If the employer pins
`.NET 8` LTS**, that is a one-line target-framework change with no architectural
impact. Do not invent a different version; default to .NET 10, switch only on the
architect's instruction.

## The eight services (one `docker-compose.yml`)

| Service | Stack | Responsibility |
|---------|-------|----------------|
| `api` | ASP.NET Core Web API (.NET 10 LTS) | HTTP surface: brands, knowledge CMS, runs, approvals, assets |
| `worker` | .NET Worker Service + Hangfire | Executes agent runs as durable jobs; checkpoint + resume |
| `frontend` | Next.js (React, TypeScript) | Analytics + approval dashboards, onboarding, run/trace viewer |
| `postgres` | `pgvector/pgvector` | Relational data, RLS isolation, embeddings, **Hangfire job store**, run checkpoints |
| `redis` | `redis` | `IDistributedCache` (caching only). **Not the queue broker.** |
| `minio` | `minio/minio` | Object storage for generated media, brand-prefixed |
| `vault` | `hashicorp/vault` (dev mode) | KV: app secrets. Transit: per-brand token encryption |
| `embeddings` | local model server (Ollama) | Serves `nomic-embed-text-v1.5` over HTTP for ingest + retrieval; self-hosted, open-source |

A compose skeleton (service names, images, dependency order) is in
`assets/docker-compose.skeleton.yml`. The demo target is `docker compose up`; no
public URL is required. Keep the architecture cloud-portable (same images →
Kubernetes, MinIO → S3, dev Vault → hardened Vault).

```
                        ┌─────────────────────────────────────────────┐
                        │              docker-compose                  │
  Browser ──────────────▶  frontend (React / Next.js)                  │
                        │      │  HTTP                                 │
                        │      ▼                                       │
                        │  api (ASP.NET Core) ──────┐                  │
                        │      │ enqueue jobs       │ EF Core (RLS)    │
                        │      ▼                    ▼                  │
                        │  postgres + pgvector  ◀── worker (.NET +     │
                        │      ▲   (Hangfire jobs,    Hangfire)        │
                        │      │    run state,        │                │
                        │      │    checkpoints,      ├──▶ minio       │
                        │      │    embeddings)       ├──▶ vault       │
                        │  redis (IDistributedCache)  ├──▶ Claude API  │
                        │                             ├──▶ Gemini API  │
                        │                             ├──▶ embeddings (nomic-v1.5) │
                        │                             └──▶ MetaIntegration (mock|live) │
                        └─────────────────────────────────────────────┘
```

## Python → .NET mapping (DL-015, authoritative)

The bootcamp standards docs are Python-specific. The **principles** transfer
(async I/O, DI, typed boundaries, structured tool errors, no secrets in code,
structured logging, tests on critical paths). The **idioms** become .NET:

| Concern | Was (Python) | Now (.NET) |
|---------|--------------|------------|
| Web framework | FastAPI | ASP.NET Core Web API (controllers by resource) |
| Async | asyncio / httpx | async/await + `IHttpClientFactory` (async-native) |
| DI | FastAPI `Depends` | built-in MS.Extensions.DependencyInjection (lifetimes) |
| Startup singletons | lifespan handler | DI Singleton + generic host / `IHostedService` |
| Config | pydantic-settings | Options pattern (`IOptions`, `ValidateOnStart`) |
| Boundary validation | Pydantic models | DTOs + FluentValidation; `[ApiController]` auto-400 |
| ORM | SQLAlchemy + asyncpg | EF Core + Npgsql |
| Migrations | Alembic (RLS SQL) | EF Core Migrations (RLS via `migrationBuilder.Sql`) |
| RLS session var | dependency `set_config` | EF `DbConnectionInterceptor` + request-scoped `IBrandContext` |
| Queue + worker | Arq on Redis | **Hangfire on PostgreSQL** + .NET Worker Service |
| Cache | (Redis/TTL) | `IDistributedCache` → Redis (or `IMemoryCache`) |
| Secrets client | env/Vault | VaultSharp (KV → Options; Transit → token crypto) |
| Object storage | MinIO SDK | Minio .NET SDK behind `IStorageService` |
| Vector store | pgvector (SQLAlchemy) | pgvector via `Pgvector.EntityFrameworkCore` |
| Embeddings | sentence-transformers (bge) | **Self-hosted nomic-embed-text-v1.5** via local model server |
| LLM orchestration | Claude SDK + Python MCP | Anthropic API from .NET + **.NET MCP SDK** (younger) |
| Media tool | Gemini SDK | Gemini via HttpClient behind `IMediaGenerationTool` |
| Tests | pytest | xUnit + WebApplicationFactory + Testcontainers |
| Lint/format/types | ruff / mypy | dotnet format + Roslyn analyzers + nullable refs |

> Note: the mapping above writes `IOptions` and `[ApiController]` without angle
> brackets only where this file is prose; in code use the real generic/attribute
> syntax. The skill frontmatter avoids angle brackets per the skill standard, but
> implementation code uses normal C#.

## Engineering principles that still bind (language-neutral)

- All external I/O is async; `IHttpClientFactory` for HTTP clients.
- Dependency injection everywhere; no globals; singletons via DI lifetimes.
- Typed boundaries: DTOs + FluentValidation in, typed results out.
- Structured tool errors returned into the loop (degrade, don't crash).
- No secrets in code, image, DB, or logs.
- Structured logging.
- Tests on critical paths; CI runs on mocks only.
