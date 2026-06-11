---
name: dotnet-engineering-standards
description: Enforces this repo's cross-cutting .NET engineering standards on every C# build task. Apply whenever writing, scaffolding, reviewing, or modifying ANY C#/.NET code here, even when not asked explicitly — on tasks such as solution or project scaffolding, ASP.NET Core controllers and endpoints, services, Hangfire workers and jobs, EF Core entities and migrations, agent nodes and tools, DTOs and validators, xUnit tests, Program.cs DI wiring, Dockerfiles, CI workflows, and pre-commit or git setup. Covers async/await correctness, dependency injection and lifetimes, the Options config pattern, FluentValidation boundary checks, Polly resilience and structured errors, structured logging, testing, secrets hygiene, dependency pinning, naming, and CI gates. Trigger on phrases like build the API, add an endpoint, write the worker, create a migration, wire up DI, add a service, set up CI, pin dependencies, or review this C# code.
---

# .NET Engineering Standards (repo-wide enforcement)

This skill is the **standards layer** for every C# build task in this repository.
It encodes the bootcamp's nine engineering standards and coding conventions as
**.NET idioms**. The Python source docs are translated, never copied.

It **composes with** — and must never restate or contradict — the architecture
skill (service topology, layered `Api`/`Core`/`Infrastructure`/`Worker`
boundaries, RLS isolation, Vault secrets **mechanism**, MinIO, pgvector) and the
orchestration skill (agent graph, `RunState`, the human gate, `ToolError`
semantics). When those decisions are relevant, **reference them; do not redefine
them.** This skill governs *how the C# is written*, not *what the system is*.

---

## CRITICAL RULES — verify these on every change before anything else

1. **Async or it doesn't ship.** Every I/O path is `async`/`await` end to end. No
   `.Result`, `.Wait()`, `.GetAwaiter().GetResult()` (sync-over-async deadlocks).
   No `Thread.Sleep` — use `await Task.Delay`. HTTP via `IHttpClientFactory`; EF
   Core via async APIs (`ToListAsync`, `SaveChangesAsync`). CPU-bound work goes
   off the request path (`Task.Run` / a job), never inline on a hot path.
2. **No secret in code, image, DB, or logs — ever.** No keys, connection
   strings, or tokens in source or `appsettings.json`. gitleaks runs in
   pre-commit and CI. Secrets load through the Options pattern / the architecture
   skill's secrets provider. Never log a token, secret, or PII.
3. **Inject, never reach.** Constructor injection only. No `static` mutable
   state, no service-locator, no `new HttpClient()` / `new DbContext()` inside a
   handler. Every dependency is registered in `Program.cs` with an explicit
   lifetime (Singleton / Scoped / Transient).
4. **Validate at the edge, then trust your types.** Every external input
   (HTTP body, tool argument, webhook) enters as a DTO with a FluentValidation
   validator. `[ApiController]` returns `400` ProblemDetails automatically.
   Nullable reference types are **on**; the domain interior assumes valid,
   non-null data.
5. **Degrade, don't crash; never leak.** External calls have timeouts and Polly
   retries (transient only). Agent tools return a structured `ToolError` into the
   loop — they do not throw into the graph. HTTP errors surface as ProblemDetails
   with correct status codes, never `200` with an error body, never a raw stack
   trace or secret to the caller. No catch-all `catch (Exception) { }` swallow.
6. **Don't redefine the architecture.** Respect the layered project boundaries
   and the secrets/storage/isolation mechanisms owned by the architecture and
   orchestration skills. This skill enforces code quality *within* them.

If a task would violate any of the above, fix the code — do not proceed.

---

## Enforcement checklist by domain

Each item is the rule. Full right/wrong examples live in `references/` — read the
linked file when you need the concrete pattern.

### 1. Async all the way down → `references/code-examples.md`
- `async`/`await` on every controller action, service method, job, and tool that
  touches I/O. No blocking calls on the event/request path.
- Parallelize independent awaits with `Task.WhenAll`.
- `IHttpClientFactory`-provided clients only; never construct `HttpClient`.
- EF Core: async query + save APIs; no synchronous materialization.
- `Thread.Sleep` → `await Task.Delay`. Sync-over-async is a defect, not a style nit.

### 2. Dependency injection + lifetimes → `references/code-examples.md`
- Constructor injection for DB context, HTTP clients, integration interfaces,
  options, loggers. No statics, no `IServiceProvider` pulled in to resolve services.
- Register everything in `Program.cs` with a deliberate lifetime: **Singleton**
  for clients/models/config, **Scoped** for per-request (DbContext, brand
  context), **Transient** otherwise.
- Startup singletons come up via DI / `IHostedService` and are disposed on
  shutdown — not created lazily inside a request.

### 3. Configuration — the Options pattern → `references/code-examples.md`
- Strongly-typed options classes bound from configuration, registered with
  `ValidateDataAnnotations()` and `ValidateOnStart()` so **missing required
  config fails at startup**, not at first use.
- No scattered `Environment.GetEnvironmentVariable` reads through the code; config
  is injected as bound options.
- The **secrets mechanism** (Vault KV → options, Vault Transit → per-brand tokens)
  is owned by the architecture skill. Reference it; do not restate or re-implement it.

### 4. Boundary validation → `references/code-examples.md`
- Request/response **DTOs** at every HTTP and tool boundary, each with a
  **FluentValidation** validator. Never bind domain entities directly to requests.
- Nullable reference types enabled solution-wide; treat warnings as errors for
  nullability.
- LLM/tool structured outputs deserialize into typed records and are validated
  before use — no free-form string parsing where a schema is possible.

### 5. Errors, resilience, failure isolation → `references/code-examples.md`
- Timeout on **every** external call (`HttpClient.Timeout` / `CancellationToken`).
- **Polly** retry with exponential backoff, on **transient** failures only
  (timeouts, 5xx, network) — never on 4xx.
- Agent tools return `ToolError(code, message, retryable)` into the loop; the
  supervisor adjudicates retry/degrade/fail. Exceptions do not cross the graph
  boundary. (Semantics owned by the orchestration skill — honor them.)
- Catch **specific** exceptions; let truly unexpected ones propagate. No bare
  catch-and-swallow.
- HTTP layer: ProblemDetails with the right status code; never expose stack
  traces, queries, file paths, or secrets to the caller.

### 6. Structured logging → `references/code-examples.md`
- `ILogger<T>` (Serilog sink) with **named structured fields**, not interpolated
  prose. No `Console.WriteLine`.
- Levels used correctly: Debug (diagnostics) / Information (normal events) /
  Warning (recoverable anomaly) / Error (operation failed) / Critical (unusable).
- Never log secrets, tokens, credentials, or PII. Log correlation/run ids instead.

### 7. Testing → `references/testing.md`
- **xUnit**. Test the critical paths, not coverage theater.
- Three tiers: DTO/FluentValidation validators (valid + invalid), services with
  **mocked integrations**, and **one** happy-path end-to-end via
  `WebApplicationFactory` + **Testcontainers** (Postgres).
- Mock external calls (Meta, Gemini, embeddings) behind their interfaces; CI runs
  on mocks only.
- Arrange–Act–Assert; descriptive test names; tests **run in CI** or they don't exist.

### 8. Code hygiene & project layout → `references/naming-and-style.md`
- Respect the **architecture skill's** layered solution
  (`Api`/`Core`/`Infrastructure`/`Worker`). Do not invent a different structure.
- `dotnet format` clean; Roslyn analyzers clean; nullable on. These are
  non-negotiable and gated in CI.
- Naming per the table in `references/naming-and-style.md` (PascalCase types/
  methods/properties/consts, camelCase locals/params, `_camelCase` private
  fields). **No snake_case anywhere.**
- README is a **map**: architecture overview, run command, required config keys
  (names only), where the interesting code lives.

### 9. Security & secrets → `references/ci-and-precommit.md`
- gitleaks pre-commit hook + CI job. `.gitignore` and `.dockerignore` exclude
  secrets, build artifacts, IDE files, and test output.
- Validate all external input at the boundary (see §4). Never trust client-side
  validation alone.

### 10. Dependency management → `references/ci-and-precommit.md`
- **Central, pinned versions** via `Directory.Packages.props` (central package
  management). No floating versions.
- Separate dev/test packages from runtime; vulnerability scan in CI; minimize the
  dependency count — don't add a package for what the BCL already does.

### 11. Git conventions → `references/git-conventions.md`
- Branches: `type/short-description` (lowercase + hyphens). **Never commit to
  `main` or `develop`.**
- Commits: Conventional Commits `type(scope): imperative summary` under 72 chars.
- PRs: `[TYPE] imperative description` title + the description template;
  under ~400 lines; one concern; squash-merge.

### 12. CI gates → `references/ci-and-precommit.md`
- Required checks on every push/PR: `dotnet build`, `dotnet test`,
  `dotnet format --verify-no-changes`, Roslyn analyzers, gitleaks.
- Thresholds must be real, and the branch ruleset **binds admins too**. A gate
  anyone can bypass is not a gate.

---

## Pre-flight self-review (run before declaring a C# task done)

- [ ] No sync-over-async, no `Thread.Sleep`, no blocking I/O on a request/job path.
- [ ] Every dependency constructor-injected and registered with an explicit lifetime.
- [ ] Required config validated at startup via Options; no scattered env reads.
- [ ] Every external input is a DTO with a FluentValidation validator; nullable on.
- [ ] Every external call: timeout + Polly transient-only retry; tools return `ToolError`.
- [ ] HTTP errors are ProblemDetails; no swallowed exceptions; no leaked traces/secrets.
- [ ] Logging is structured `ILogger`; no secrets/PII/`Console.WriteLine`.
- [ ] Critical paths tested (validators, mocked services, one E2E); tests run in CI.
- [ ] `dotnet format` + analyzers clean; naming per the table; no snake_case.
- [ ] No secret in code/image/DB/logs; gitleaks, `.gitignore`, `.dockerignore` in place.
- [ ] Dependencies pinned centrally; dev/runtime split.
- [ ] Branch/commit/PR follow the conventions; CI gates present and bind admins.

If every box is checked, the code meets the standard. If not, fix before shipping.
