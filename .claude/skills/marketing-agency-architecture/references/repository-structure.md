# Repository Structure (monorepo)

Governs the folder layout. Immutable input. Layering follows the canonical .NET
Api/Core/Infrastructure separation, mapping the bootcamp's "split-by-responsibility"
principle to the idiom a .NET reviewer expects.

```
project-root/
├── backend/
│   ├── src/
│   │   ├── Api/                 # ASP.NET Core Web API
│   │   │   ├── Program.cs       # builder, DI registration, middleware, Hangfire client
│   │   │   ├── Controllers/     # BrandsController, KnowledgeController, RunsController,
│   │   │   │                    #   ApprovalsController, AssetsController, HealthController
│   │   │   └── Dtos/            # request/response DTOs (+ FluentValidation validators)
│   │   ├── Worker/             # .NET Worker Service
│   │   │   ├── Program.cs       # Hangfire server host
│   │   │   └── Jobs/            # ExecuteRunJob, ResumeRunJob
│   │   ├── Core/               # domain: entities, value objects, interfaces, agent contracts
│   │   └── Infrastructure/     # EF Core DbContext + Migrations (incl. RLS SQL), Vault,
│   │                           #   MinIO, Meta/Media integrations, retrieval, embeddings
│   ├── tests/
│   │   ├── UnitTests/           # DTO/validator + service tests (mocked integrations)
│   │   └── IntegrationTests/    # WebApplicationFactory + Testcontainers (Postgres)
│   ├── Backend.sln
│   └── Dockerfile               # builds api + worker from one publish output
├── frontend/                    # Next.js (React, TypeScript) dashboard
│   ├── app/                     # routes: onboarding, analytics, approvals, run/trace viewer
│   ├── components/
│   ├── lib/api-client.ts        # typed API client (no business logic here)
│   ├── package.json
│   └── Dockerfile
├── docker-compose.yml
├── appsettings.Example.json
├── .env.example
└── README.md
```

## Layering rules

- Interfaces and domain in `Core`; implementations in `Infrastructure`.
- `Api` and `Worker` both reference `Core` + `Infrastructure`; built from one
  publish output (zero API↔worker skew).
- **EF Core Migrations own the RLS policies** (raw SQL via `migrationBuilder.Sql`) —
  isolation is versioned schema, not a manual step.
- Hangfire job store is a **separate Postgres schema**.
- Frontend has **no business logic**; it talks to the API over HTTP via the typed
  client in `lib/`; brand scope and all data come from the API.

## CI

GitHub Actions: `dotnet build`, `dotnet test`, `dotnet format`, Roslyn analyzers,
gitleaks. **CI runs on mocks only** (no live keys/network). Phase 9 adds eval gates
tied to real thresholds; admins are not exempt from the rulesets.
