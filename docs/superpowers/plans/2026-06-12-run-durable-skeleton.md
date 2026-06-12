# Run Durable Skeleton Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove the durable loop thin: `POST /runs` → Hangfire `ExecuteRun` → stub orchestrator → checkpoint at gate → job ends → `POST /runs/{id}/approval` → `ResumeRun` in a fresh scope → done. Agent intelligence, MAF, and all external I/O are OUT of this slice.

**Architecture:** `ExecuteRunJob` runs the stub generation spine and checkpoints `RunState` as JSON in `RunCheckpoint`; the job then sets `AgentRun = AwaitingApproval` and returns — nothing held in memory. `ResumeRunJob` is a separate Hangfire execution that reads the checkpoint from Postgres, rehydrates `RunState`, runs the stub publish step, and sets `AgentRun = Done`. The durable seam is Postgres, not process memory.

**Tech Stack:** .NET 10, ASP.NET Core, Hangfire.PostgreSql 1.20.10 + Hangfire.AspNetCore 1.8.14, EF Core 9 + Npgsql, xUnit + Testcontainers (pgvector:pg16), System.Text.Json with `JsonStringEnumConverter`.

---

## File inventory

### New files — Core/Orchestration
- `backend/src/Core/Orchestration/GraphPhase.cs` — resume marker enum
- `backend/src/Core/Orchestration/Budget.cs` — token + media budget record
- `backend/src/Core/Orchestration/TraceRefs.cs` — Langfuse trace refs (stub values for this slice)
- `backend/src/Core/Orchestration/ToolError.cs` — structured tool error record
- `backend/src/Core/Orchestration/Contracts/ContentStrategy.cs`
- `backend/src/Core/Orchestration/Contracts/CreativeDirection.cs`
- `backend/src/Core/Orchestration/Contracts/Caption.cs`
- `backend/src/Core/Orchestration/Contracts/MediaAssetRef.cs`
- `backend/src/Core/Orchestration/Contracts/ContentItemDraft.cs`
- `backend/src/Core/Orchestration/Contracts/PublishResult.cs`
- `backend/src/Core/Orchestration/RunState.cs` — typed graph state threaded through the run
- `backend/src/Core/Orchestration/IOrchestrator.cs` — generation + publish seam interface

### Modified files — Core
- `backend/src/Core/Domain/RunStatus.cs` — add `Rejected` terminal state

### New files — Infrastructure/Jobs
- `backend/src/Infrastructure/Jobs/RunStateJsonOptions.cs` — shared `JsonSerializerOptions`
- `backend/src/Infrastructure/Jobs/ExecuteRunJob.cs` — Hangfire job: generation spine → checkpoint → AwaitingApproval
- `backend/src/Infrastructure/Jobs/ResumeRunJob.cs` — Hangfire job: rehydrate → publish stub → Done
- `backend/src/Infrastructure/Jobs/HangfireServiceCollectionExtensions.cs` — `AddHangfireJobStore` / `AddHangfireWorker`

### New files — Infrastructure/Orchestration
- `backend/src/Infrastructure/Orchestration/StubOrchestrator.cs` — placeholder outputs, no external I/O
- `backend/src/Infrastructure/Orchestration/OrchestrationServiceCollectionExtensions.cs` — `AddOrchestration()`

### Modified files — Infrastructure
- `backend/src/Infrastructure/Infrastructure.csproj` — add `Hangfire.AspNetCore` + `Hangfire.PostgreSql`
- `backend/src/Worker/Worker.csproj` — add `Hangfire.AspNetCore`
- `backend/src/Worker/Program.cs` — wire `AddDataAccess`, `AddHangfireJobStore`, `AddHangfireWorker`, `AddOrchestration`
- `backend/src/Api/Program.cs` — add `AddHangfireJobStore`, `AddOrchestration`, `BrandContextMiddleware`

### New files — Api
- `backend/src/Api/Middleware/BrandContextMiddleware.cs` — reads `X-Brand-Id` header, binds `IBrandContext`
- `backend/src/Api/Dtos/CreateRunResponse.cs`
- `backend/src/Api/Dtos/ApprovalRequest.cs`
- `backend/src/Api/Dtos/ApprovalRequestValidator.cs`
- `backend/src/Api/Dtos/RunStatusResponse.cs`
- `backend/src/Api/Controllers/RunsController.cs`

### New files — Tests
- `backend/tests/IntegrationTests/Durability/DurabilityFixture.cs`
- `backend/tests/IntegrationTests/Durability/DurabilityTests.cs`

---

## Task 0: Branch setup

**Files:** (git operations only)

- [ ] **Step 1: Verify feat/brand-onboarding is committed and pushed**

```bash
cd "C:/Users/Jamal/Documents/bootcamps/AIE/capstone/quorums"
git status
git log --oneline -5
```

Expected: working tree clean on `feat/brand-onboarding`.

- [ ] **Step 2: Open PR for feat/brand-onboarding (if not already open)**

```bash
gh pr create \
  --title "[FEAT] brand onboarding slice: POST /brands with self-scoped RLS" \
  --base main \
  --head feat/brand-onboarding \
  --body "$(cat <<'EOF'
## Summary
- Expands `BrandProfile` to structured identity shape consumed by orchestration agents
- Implements `POST /brands` with self-scoping: handler generates new brandId, binds IBrandContext, runs through FORCE RLS
- Returns 201 with new brand id; DTOs + FluentValidation at boundary
- Also includes DL-016 Ollama Compose profile amendment (gated behind `--profile embeddings`)

## Test plan
- [ ] Validator unit tests (12 tests)
- [ ] Happy-path integration test
- [ ] Self-scoping isolation proof (Category=Isolation)
- [ ] Cross-brand insert rejection (Category=Isolation)
- [ ] dotnet build -warnaserror green
- [ ] dotnet format --verify-no-changes clean
- [ ] dotnet test green

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Step 3: Merge feat/brand-onboarding into main**

Merge via GitHub UI (squash merge), or:

```bash
git checkout main
git pull origin main
git merge --no-ff feat/brand-onboarding -m "Merge feat/brand-onboarding into main"
git push origin main
```

- [ ] **Step 4: Create feature/run-durable-skeleton off updated main**

```bash
git checkout main
git pull origin main
git checkout -b feature/run-durable-skeleton
git push -u origin feature/run-durable-skeleton
```

- [ ] **Step 5: Verify baseline build is green**

```bash
cd "C:/Users/Jamal/Documents/bootcamps/AIE/capstone/quorums/backend"
dotnet build Backend.sln -warnaserror
```

Expected: Build succeeded, 0 warnings.

- [ ] **Step 6: Commit branch setup**

```bash
git commit --allow-empty -m "chore: open feature/run-durable-skeleton off main"
```

---

## Task 1: Core orchestration contracts

**Files:**
- Create: `backend/src/Core/Orchestration/GraphPhase.cs`
- Create: `backend/src/Core/Orchestration/Budget.cs`
- Create: `backend/src/Core/Orchestration/TraceRefs.cs`
- Create: `backend/src/Core/Orchestration/ToolError.cs`
- Create: `backend/src/Core/Orchestration/Contracts/ContentStrategy.cs`
- Create: `backend/src/Core/Orchestration/Contracts/CreativeDirection.cs`
- Create: `backend/src/Core/Orchestration/Contracts/Caption.cs`
- Create: `backend/src/Core/Orchestration/Contracts/MediaAssetRef.cs`
- Create: `backend/src/Core/Orchestration/Contracts/ContentItemDraft.cs`
- Create: `backend/src/Core/Orchestration/Contracts/PublishResult.cs`
- Create: `backend/src/Core/Orchestration/RunState.cs`
- Create: `backend/src/Core/Orchestration/IOrchestrator.cs`
- Modify: `backend/src/Core/Domain/RunStatus.cs`

- [ ] **Step 1: Add `Rejected` to RunStatus**

`backend/src/Core/Domain/RunStatus.cs` — no migration needed (column is `varchar(32)` with no CHECK constraint):

```csharp
namespace Backend.Core.Domain;

/// <summary>
/// Lifecycle of an <see cref="AgentRun"/> (DL-006). The supervisor is the sole
/// writer of this value; agents never advance it directly.
/// </summary>
public enum RunStatus
{
    Queued,
    Running,
    AwaitingApproval,
    Publishing,
    Done,
    Failed,
    Rejected,
}
```

- [ ] **Step 2: Create GraphPhase.cs**

`backend/src/Core/Orchestration/GraphPhase.cs`:

```csharp
namespace Backend.Core.Orchestration;

/// <summary>
/// The single resume marker persisted in <see cref="RunState.Phase"/> (DL-020).
/// <see cref="ResumeRun"/> reads this to re-enter the graph at the correct node.
/// </summary>
public enum GraphPhase
{
    Strategy,
    Creative,
    Generation,
    Assembled,
    AwaitingApproval,
    Publishing,
    Done,
}
```

- [ ] **Step 3: Create Budget.cs**

`backend/src/Core/Orchestration/Budget.cs`:

```csharp
namespace Backend.Core.Orchestration;

/// <summary>Token and media spend envelope checked before expensive graph nodes (DL-023).</summary>
public sealed record Budget(
    int TokenBudget,
    int TokensSpent,
    decimal MediaBudget,
    decimal MediaSpent);
```

- [ ] **Step 4: Create TraceRefs.cs**

`backend/src/Core/Orchestration/TraceRefs.cs`:

```csharp
namespace Backend.Core.Orchestration;

/// <summary>Langfuse trace/span ids threaded through RunState (stubbed in this slice).</summary>
public sealed record TraceRefs(
    string TraceId,
    List<string> SpanIds);
```

- [ ] **Step 5: Create ToolError.cs**

`backend/src/Core/Orchestration/ToolError.cs`:

```csharp
namespace Backend.Core.Orchestration;

/// <summary>
/// Structured tool error returned into the graph; never thrown as an exception (DL-022).
/// The Supervisor adjudicates retry/degrade/fail per node.
/// </summary>
public sealed record ToolError(
    string Code,
    string Message,
    bool Retryable);
```

- [ ] **Step 6: Create contract records**

`backend/src/Core/Orchestration/Contracts/ContentStrategy.cs`:

```csharp
namespace Backend.Core.Orchestration.Contracts;

/// <summary>Content Strategist output: what to say (DL-019).</summary>
public sealed record ContentStrategy(
    string Pillar,
    string Angle,
    string Objective,
    string Audience,
    string? CalendarSlot);
```

`backend/src/Core/Orchestration/Contracts/CreativeDirection.cs`:

```csharp
namespace Backend.Core.Orchestration.Contracts;

/// <summary>Creative Director output: how it looks (DL-019).</summary>
public sealed record CreativeDirection(
    string VisualConcept,
    List<string> StyleTokens,
    List<string> ColorTokens,
    string MediaPromptBrief);
```

`backend/src/Core/Orchestration/Contracts/Caption.cs`:

```csharp
namespace Backend.Core.Orchestration.Contracts;

/// <summary>Copywriting output: Instagram caption (DL-019).</summary>
public sealed record Caption(
    string Hook,
    string Body,
    List<string> Hashtags);
```

`backend/src/Core/Orchestration/Contracts/MediaAssetRef.cs`:

```csharp
namespace Backend.Core.Orchestration.Contracts;

/// <summary>
/// Media Generation output: pointer to the stored asset (DL-019).
/// In this slice the storage key is a placeholder; MinIO write is c2 scope.
/// </summary>
public sealed record MediaAssetRef(
    Guid AssetId,
    string StorageKey,
    string Modality,
    string MimeType);
```

`backend/src/Core/Orchestration/Contracts/ContentItemDraft.cs`:

```csharp
namespace Backend.Core.Orchestration.Contracts;

/// <summary>Supervisor assembly output: the assembled draft before the gate (DL-019).</summary>
public sealed record ContentItemDraft(
    Caption CaptionRef,
    MediaAssetRef? MediaRef,
    Guid BrandId,
    string Status);
```

`backend/src/Core/Orchestration/Contracts/PublishResult.cs`:

```csharp
namespace Backend.Core.Orchestration.Contracts;

/// <summary>Publishing output: result of the mock Meta publish (DL-019).</summary>
public sealed record PublishResult(
    string? ExternalRef,
    string Status,
    string? Error);
```

- [ ] **Step 7: Create RunState.cs**

`backend/src/Core/Orchestration/RunState.cs`:

```csharp
using Backend.Core.Domain;
using Backend.Core.Orchestration.Contracts;

namespace Backend.Core.Orchestration;

/// <summary>
/// Typed graph state threaded through the run and persisted as <see cref="RunCheckpoint"/>
/// JSON so the durable pause/resume seam in Postgres works (DL-020).
/// The Supervisor is the ONLY writer of <see cref="Phase"/>, <see cref="Draft"/>,
/// and <see cref="Budget"/>; each agent writes only its declared slice.
/// </summary>
public sealed record RunState(
    Guid RunId,
    Guid BrandId,
    GraphPhase Phase,
    ContentStrategy? Strategy,
    CreativeDirection? Creative,
    Caption? Caption,
    MediaAssetRef? Media,
    ContentItemDraft? Draft,
    ApprovalDecision? Approval,
    PublishResult? Publish,
    Budget Budget,
    List<ToolError> Errors,
    TraceRefs Trace);
```

- [ ] **Step 8: Create IOrchestrator.cs**

`backend/src/Core/Orchestration/IOrchestrator.cs`:

```csharp
namespace Backend.Core.Orchestration;

/// <summary>
/// Runs the MAF graph within a single Hangfire segment. Phase 1 stub: no MAF,
/// no external I/O. Phase 2 will replace this with the real supervised graph.
/// </summary>
public interface IOrchestrator
{
    /// <summary>
    /// Runs the generation spine (Strategy → Creative → Copy ∥ Media → Assembly).
    /// Returns RunState with Phase = AwaitingApproval ready for the gate.
    /// </summary>
    Task<RunState> RunGenerationAsync(RunState state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs the publish step (ResumeRun path).
    /// Returns RunState with Phase = Done.
    /// </summary>
    Task<RunState> RunPublishAsync(RunState state, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 9: Build to verify contracts compile**

```bash
cd "C:/Users/Jamal/Documents/bootcamps/AIE/capstone/quorums/backend"
dotnet build Backend.sln -warnaserror
```

Expected: Build succeeded, 0 warnings.

- [ ] **Step 10: Commit**

```bash
git add backend/src/Core/
git commit -m "feat(orchestration): Core RunState contracts and IOrchestrator seam (DL-020)"
```

---

## Task 2: Infrastructure stubs + package refs (compile foundation for tests)

**Files:**
- Modify: `backend/src/Infrastructure/Infrastructure.csproj`
- Modify: `backend/src/Worker/Worker.csproj`
- Create: `backend/src/Infrastructure/Jobs/RunStateJsonOptions.cs`
- Create: `backend/src/Infrastructure/Jobs/ExecuteRunJob.cs` (stub)
- Create: `backend/src/Infrastructure/Jobs/ResumeRunJob.cs` (stub)
- Create: `backend/src/Infrastructure/Orchestration/StubOrchestrator.cs`

**Why stubs first:** The DurabilityFixture constructs job instances directly. The jobs need to compile (with correct constructor signatures) before the test file can compile. The `NotImplementedException` stubs give us the RED state at runtime.

- [ ] **Step 1: Add Hangfire packages to Infrastructure.csproj**

Add inside the `<ItemGroup>` with existing package refs:

```xml
<PackageReference Include="Hangfire.AspNetCore" />
<PackageReference Include="Hangfire.PostgreSql" />
```

- [ ] **Step 2: Add Hangfire.AspNetCore to Worker.csproj**

```xml
<PackageReference Include="Hangfire.AspNetCore" />
```

- [ ] **Step 3: Create RunStateJsonOptions.cs**

`backend/src/Infrastructure/Jobs/RunStateJsonOptions.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backend.Infrastructure.Jobs;

/// <summary>Shared options for RunState ↔ JSON round-trips through RunCheckpoint.</summary>
internal static class RunStateJsonOptions
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
```

- [ ] **Step 4: Create ExecuteRunJob.cs stub**

`backend/src/Infrastructure/Jobs/ExecuteRunJob.cs`:

```csharp
using Backend.Core.Multitenancy;
using Backend.Core.Orchestration;
using Backend.Infrastructure.Persistence;

namespace Backend.Infrastructure.Jobs;

/// <summary>
/// Hangfire job: generation spine → checkpoint RunState → AgentRun = AwaitingApproval → job ends.
/// Brand context is bound from the job payload (runId, brandId) before any DB access (DL-007).
/// </summary>
public sealed class ExecuteRunJob
{
    private readonly AppDbContext _db;
    private readonly IBrandScope _scope;
    private readonly IBrandContext _brandContext;
    private readonly IOrchestrator _orchestrator;

    public ExecuteRunJob(
        AppDbContext db,
        IBrandScope scope,
        IBrandContext brandContext,
        IOrchestrator orchestrator)
    {
        _db = db;
        _scope = scope;
        _brandContext = brandContext;
        _orchestrator = orchestrator;
    }

    public Task ExecuteAsync(Guid runId, Guid brandId, CancellationToken cancellationToken = default)
        => throw new NotImplementedException("ExecuteRunJob not yet implemented");
}
```

- [ ] **Step 5: Create ResumeRunJob.cs stub**

`backend/src/Infrastructure/Jobs/ResumeRunJob.cs`:

```csharp
using Backend.Core.Multitenancy;
using Backend.Core.Orchestration;
using Backend.Infrastructure.Persistence;

namespace Backend.Infrastructure.Jobs;

/// <summary>
/// Hangfire job: rehydrate RunState from RunCheckpoint → publish stub → AgentRun = Done.
/// Brand context is bound from the job payload before any DB access (DL-007).
/// </summary>
public sealed class ResumeRunJob
{
    private readonly AppDbContext _db;
    private readonly IBrandScope _scope;
    private readonly IBrandContext _brandContext;
    private readonly IOrchestrator _orchestrator;

    public ResumeRunJob(
        AppDbContext db,
        IBrandScope scope,
        IBrandContext brandContext,
        IOrchestrator orchestrator)
    {
        _db = db;
        _scope = scope;
        _brandContext = brandContext;
        _orchestrator = orchestrator;
    }

    public Task ExecuteAsync(Guid runId, Guid brandId, CancellationToken cancellationToken = default)
        => throw new NotImplementedException("ResumeRunJob not yet implemented");
}
```

- [ ] **Step 6: Create StubOrchestrator.cs**

`backend/src/Infrastructure/Orchestration/StubOrchestrator.cs`:

```csharp
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;

namespace Backend.Infrastructure.Orchestration;

/// <summary>
/// Placeholder orchestrator for the durable-skeleton slice. Produces deterministic
/// in-memory outputs with no external I/O. Replaced by the real MAF graph in Phase 2.
/// </summary>
public sealed class StubOrchestrator : IOrchestrator
{
    public Task<RunState> RunGenerationAsync(RunState state, CancellationToken cancellationToken = default)
    {
        var strategy = new ContentStrategy(
            Pillar: "stub-pillar",
            Angle: "stub-angle",
            Objective: "stub-objective",
            Audience: "stub-audience",
            CalendarSlot: null);

        var creative = new CreativeDirection(
            VisualConcept: "stub-concept",
            StyleTokens: ["soft"],
            ColorTokens: ["#ffffff"],
            MediaPromptBrief: "stub-brief");

        var caption = new Caption(
            Hook: "stub-hook",
            Body: "stub-body",
            Hashtags: ["#stub"]);

        var media = new MediaAssetRef(
            AssetId: Guid.NewGuid(),
            StorageKey: $"brands/{state.BrandId}/assets/stub",
            Modality: "image",
            MimeType: "image/png");

        var draft = new ContentItemDraft(
            CaptionRef: caption,
            MediaRef: media,
            BrandId: state.BrandId,
            Status: "pending");

        return Task.FromResult(state with
        {
            Phase = GraphPhase.AwaitingApproval,
            Strategy = strategy,
            Creative = creative,
            Caption = caption,
            Media = media,
            Draft = draft,
        });
    }

    public Task<RunState> RunPublishAsync(RunState state, CancellationToken cancellationToken = default)
    {
        var result = new PublishResult(
            ExternalRef: null,
            Status: "stub-published",
            Error: null);

        return Task.FromResult(state with
        {
            Phase = GraphPhase.Done,
            Publish = result,
        });
    }
}
```

- [ ] **Step 7: Build to verify compilation**

```bash
cd "C:/Users/Jamal/Documents/bootcamps/AIE/capstone/quorums/backend"
dotnet build Backend.sln -warnaserror
```

Expected: Build succeeded.

- [ ] **Step 8: Commit**

```bash
git add backend/src/Infrastructure/ backend/src/Worker/Worker.csproj
git commit -m "feat(jobs): ExecuteRunJob + ResumeRunJob stubs + StubOrchestrator + Hangfire pkgs"
```

---

## Task 3: Write failing Durability tests (RED)

**Files:**
- Create: `backend/tests/IntegrationTests/Durability/DurabilityFixture.cs`
- Create: `backend/tests/IntegrationTests/Durability/DurabilityTests.cs`

- [ ] **Step 1: Create DurabilityFixture.cs**

`backend/tests/IntegrationTests/Durability/DurabilityFixture.cs`:

```csharp
using Backend.Core.Domain;
using Backend.Infrastructure.Jobs;
using Backend.Infrastructure.Multitenancy;
using Backend.Infrastructure.Orchestration;
using Backend.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Backend.IntegrationTests.Durability;

/// <summary>
/// Testcontainers fixture for the durable-skeleton tests. Starts a disposable
/// pgvector Postgres, applies migrations (so real RLS policies are active), creates
/// a non-superuser non-owner role, and seeds two brands. Provides factory methods
/// that return FRESH instances of the job classes with no shared state, proving the
/// durable seam is Postgres and not in-process memory.
/// </summary>
public sealed class DurabilityFixture : IAsyncLifetime
{
    private const string AppRole = "app_user";
    private readonly string _appPassword = Guid.NewGuid().ToString("N");

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg16")
        .Build();

    public string SuperuserConnectionString { get; private set; } = string.Empty;
    public string AppUserConnectionString { get; private set; } = string.Empty;

    public Guid BrandA { get; } = Guid.NewGuid();
    public Guid BrandB { get; } = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        SuperuserConnectionString = _container.GetConnectionString();
        await ApplyMigrationsAsync();
        await CreateLeastPrivilegeRoleAsync();
        AppUserConnectionString = BuildAppUserConnectionString();
        await SeedBrandsAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>
    /// Seeds an AgentRun for the given brand via the superuser (bypasses RLS so
    /// either brand can be seeded regardless of context).
    /// </summary>
    public async Task<Guid> SeedAgentRunAsync(Guid brandId, RunStatus status = RunStatus.Queued)
    {
        await using var db = CreateDbContext(SuperuserConnectionString);
        var run = new AgentRun
        {
            Id = Guid.NewGuid(),
            BrandId = brandId,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.AgentRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    /// <summary>
    /// Mirrors the controller's approve transition: AwaitingApproval → Publishing +
    /// writes an ApprovalAction audit record. Must be called between ExecuteRun and
    /// ResumeRun in any test that exercises the resume path, so ResumeRunJob's
    /// Publishing-only guard is satisfied exactly as in production.
    /// </summary>
    public async Task ApproveRunAsync(Guid runId, Guid brandId)
    {
        var (db, scope) = CreateReadContext(brandId);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();
            var run = await db.AgentRuns.FirstAsync(r => r.Id == runId);
            run.Status = RunStatus.Publishing;
            run.UpdatedAt = DateTimeOffset.UtcNow;
            db.ApprovalActions.Add(new ApprovalAction
            {
                Id = Guid.NewGuid(),
                BrandId = brandId,
                AgentRunId = runId,
                Decision = ApprovalDecision.Approved,
                DecidedBy = "test",
                DecidedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            await handle.CompleteAsync();
        }
    }

    /// <summary>
    /// Returns a FRESH (AppDbContext, ExecuteRunJob) pair bound to brandId via the
    /// RLS-subject role. The job's Execute method binds the brand context internally.
    /// </summary>
    public (AppDbContext Db, ExecuteRunJob Job) CreateExecuteRunJob(Guid brandId)
    {
        var db = CreateAppDbContext();
        var brandContext = new BrandContext();    // unbound — job binds it
        var scope = new BrandScope(db, brandContext);
        var orchestrator = new StubOrchestrator();
        return (db, new ExecuteRunJob(db, scope, brandContext, orchestrator));
    }

    /// <summary>
    /// Returns a FRESH (AppDbContext, ResumeRunJob) pair. Using a different DbContext
    /// instance than ExecuteRun proves zero in-process state is carried over.
    /// </summary>
    public (AppDbContext Db, ResumeRunJob Job) CreateResumeRunJob(Guid brandId)
    {
        var db = CreateAppDbContext();
        var brandContext = new BrandContext();
        var scope = new BrandScope(db, brandContext);
        var orchestrator = new StubOrchestrator();
        return (db, new ResumeRunJob(db, scope, brandContext, orchestrator));
    }

    /// <summary>Returns a read-only scoped context for assertion reads.</summary>
    public (AppDbContext Db, IBrandScope Scope) CreateReadContext(Guid brandId)
    {
        var db = CreateAppDbContext();
        var brandContext = new BrandContext();
        brandContext.Bind(brandId);
        return (db, new BrandScope(db, brandContext));
    }

    private AppDbContext CreateAppDbContext() => CreateDbContext(AppUserConnectionString);

    private static AppDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new AppDbContext(options);
    }

    private async Task ApplyMigrationsAsync()
    {
        await using var context = CreateDbContext(SuperuserConnectionString);
        await context.Database.MigrateAsync();
    }

    private async Task CreateLeastPrivilegeRoleAsync()
    {
        await using var admin = CreateDbContext(SuperuserConnectionString);
        var roleSetup =
            $"""
             DROP ROLE IF EXISTS {AppRole};
             CREATE ROLE {AppRole} LOGIN PASSWORD '{_appPassword}'
                 NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
             GRANT USAGE ON SCHEMA public TO {AppRole};
             GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {AppRole};
             """;
        await admin.Database.ExecuteSqlRawAsync(roleSetup);
    }

    private string BuildAppUserConnectionString() =>
        new NpgsqlConnectionStringBuilder(SuperuserConnectionString)
        {
            Username = AppRole,
            Password = _appPassword,
        }.ConnectionString;

    private async Task SeedBrandsAsync()
    {
        await using var seed = CreateDbContext(SuperuserConnectionString);
        var now = DateTimeOffset.UtcNow;
        seed.Brands.AddRange(
            new Brand { Id = BrandA, Name = "Brand A", CreatedAt = now },
            new Brand { Id = BrandB, Name = "Brand B", CreatedAt = now });
        await seed.SaveChangesAsync();
    }
}
```

- [ ] **Step 2: Create DurabilityTests.cs**

`backend/tests/IntegrationTests/Durability/DurabilityTests.cs`:

```csharp
using Backend.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Backend.IntegrationTests.Durability;

/// <summary>
/// Durability-seam tests. Every test proves a distinct invariant of the
/// checkpoint/resume contract (DL-006, DL-021). No HTTP, no Hangfire server —
/// job classes are constructed directly to prove zero in-process state crosses
/// the execution boundary.
/// </summary>
[Trait("Category", "Durability")]
public sealed class DurabilityTests : IClassFixture<DurabilityFixture>
{
    private readonly DurabilityFixture _fixture;

    public DurabilityTests(DurabilityFixture fixture) => _fixture = fixture;

    /// <summary>
    /// DURABLE-RESUME PROOF part 1: ExecuteRun reaches the gate, checkpoints RunState
    /// to Postgres, sets AgentRun = AwaitingApproval, and the job RETURNS — nothing
    /// held in memory. A fresh read context (new DbContext, new brand scope) sees the
    /// persisted state.
    /// </summary>
    [Fact]
    public async Task ExecuteRun_checkpoints_and_ends_with_awaiting_approval()
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA);

        var (execDb, execJob) = _fixture.CreateExecuteRunJob(_fixture.BrandA);
        await using (execDb)
        {
            await execJob.ExecuteAsync(runId, _fixture.BrandA);
        } // execDb disposed here — proves checkpoint was committed before dispose

        // Completely fresh scope: new DbContext, new BrandScope, no shared references
        var (readDb, scope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (readDb)
        {
            await using var handle = await scope.BeginAsync();

            var run = await readDb.AgentRuns.AsNoTracking()
                .FirstAsync(r => r.Id == runId);
            Assert.Equal(RunStatus.AwaitingApproval, run.Status);

            var checkpoint = await readDb.RunCheckpoints.AsNoTracking()
                .FirstOrDefaultAsync(c => c.AgentRunId == runId);
            Assert.NotNull(checkpoint);
            Assert.False(string.IsNullOrWhiteSpace(checkpoint.StateJson));
        }
    }

    /// <summary>
    /// DURABLE-RESUME PROOF part 2: ResumeRun in a FRESH execution scope (new
    /// DbContext, zero carried-over in-process state) reconstructs solely from
    /// RunCheckpoint and advances AgentRun to Done.
    /// </summary>
    [Fact]
    public async Task ResumeRun_in_fresh_scope_reconstructs_from_checkpoint_and_reaches_done()
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA);

        // Phase 1: ExecuteRun — scope 1
        var (execDb, execJob) = _fixture.CreateExecuteRunJob(_fixture.BrandA);
        await using (execDb) { await execJob.ExecuteAsync(runId, _fixture.BrandA); }

        // Simulate approval: mirrors the controller's AwaitingApproval → Publishing transition
        // (writes ApprovalAction + sets Publishing). Required so ResumeRun's Publishing-only guard passes.
        await _fixture.ApproveRunAsync(runId, _fixture.BrandA);

        // Phase 2: ResumeRun — scope 2 (fresh DbContext, fresh BrandContext, fresh BrandScope)
        var (resumeDb, resumeJob) = _fixture.CreateResumeRunJob(_fixture.BrandA);
        await using (resumeDb) { await resumeJob.ExecuteAsync(runId, _fixture.BrandA); }

        // Verification — scope 3
        var (readDb, scope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (readDb)
        {
            await using var handle = await scope.BeginAsync();
            var run = await readDb.AgentRuns.AsNoTracking()
                .FirstAsync(r => r.Id == runId);
            Assert.Equal(RunStatus.Done, run.Status);
        }
    }

    /// <summary>
    /// RE-RUN SAFETY: invoking ResumeRun a second time from the same checkpoint
    /// is a no-op — the status guard (not AwaitingApproval) returns early.
    /// AgentRun stays Done; exactly one checkpoint row exists (not duplicated).
    /// </summary>
    [Fact]
    public async Task ResumeRun_twice_is_idempotent_no_duplicate_checkpoint()
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA);

        var (execDb, execJob) = _fixture.CreateExecuteRunJob(_fixture.BrandA);
        await using (execDb) { await execJob.ExecuteAsync(runId, _fixture.BrandA); }

        // Approve: transitions AwaitingApproval → Publishing so ResumeRun's guard is satisfied.
        await _fixture.ApproveRunAsync(runId, _fixture.BrandA);

        var (r1Db, r1Job) = _fixture.CreateResumeRunJob(_fixture.BrandA);
        await using (r1Db) { await r1Job.ExecuteAsync(runId, _fixture.BrandA); }

        // Second invocation — status is now Done so the Publishing-only guard returns early
        var (r2Db, r2Job) = _fixture.CreateResumeRunJob(_fixture.BrandA);
        await using (r2Db) { await r2Job.ExecuteAsync(runId, _fixture.BrandA); }

        var (readDb, scope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (readDb)
        {
            await using var handle = await scope.BeginAsync();

            var run = await readDb.AgentRuns.AsNoTracking()
                .FirstAsync(r => r.Id == runId);
            Assert.Equal(RunStatus.Done, run.Status);

            var checkpoints = await readDb.RunCheckpoints.AsNoTracking()
                .Where(c => c.AgentRunId == runId)
                .ToListAsync();
            Assert.Single(checkpoints);
        }
    }

    /// <summary>
    /// REJECT PATH: a run in the Rejected terminal state cannot be resumed.
    /// ResumeRun exits early (status guard); AgentRun stays Rejected.
    /// </summary>
    [Fact]
    public async Task Rejected_run_is_terminal_and_ResumeRun_is_a_noop()
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA, RunStatus.Rejected);

        var (resumeDb, resumeJob) = _fixture.CreateResumeRunJob(_fixture.BrandA);
        await using (resumeDb) { await resumeJob.ExecuteAsync(runId, _fixture.BrandA); }

        var (readDb, scope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (readDb)
        {
            await using var handle = await scope.BeginAsync();
            var run = await readDb.AgentRuns.AsNoTracking()
                .FirstAsync(r => r.Id == runId);
            Assert.Equal(RunStatus.Rejected, run.Status); // unchanged
        }
    }

    /// <summary>
    /// WORKER SCOPING: a job execution scoped to Brand A cannot read Brand B's
    /// AgentRun rows. RLS filters them out even when queried by primary key.
    /// </summary>
    [Fact]
    [Trait("Category", "Isolation")]
    public async Task Worker_scoped_to_brand_A_cannot_read_brand_B_run()
    {
        var brandBRunId = await _fixture.SeedAgentRunAsync(_fixture.BrandB);

        // Open a Brand-A scope: RLS set to BrandA
        var (db, scope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            var brandBRun = await db.AgentRuns.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == brandBRunId);
            Assert.Null(brandBRun); // RLS prevents cross-brand visibility
        }
    }
}
```

- [ ] **Step 3: Run tests to verify RED**

```bash
cd "C:/Users/Jamal/Documents/bootcamps/AIE/capstone/quorums/backend"
dotnet test --filter Category=Durability -- -v normal 2>&1 | tail -30
```

Expected: All 4 Durability tests FAIL with `NotImplementedException`. The isolation test fails or passes (it doesn't call the job methods). Compilation must succeed — if you see a build error, fix it before proceeding.

- [ ] **Step 4: Commit RED tests**

```bash
git add backend/tests/IntegrationTests/Durability/
git commit -m "test(durability): durability + isolation tests RED — seam not yet implemented"
```

---

## Task 4: Implement ExecuteRunJob + ResumeRunJob (GREEN)

**Files:**
- Modify: `backend/src/Infrastructure/Jobs/ExecuteRunJob.cs`
- Modify: `backend/src/Infrastructure/Jobs/ResumeRunJob.cs`

- [ ] **Step 1: Implement ExecuteRunJob.cs**

Replace the entire file:

```csharp
using System.Text.Json;
using Backend.Core.Domain;
using Backend.Core.Multitenancy;
using Backend.Core.Orchestration;
using Backend.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Backend.Infrastructure.Jobs;

/// <summary>
/// Hangfire job: generation spine → checkpoint RunState → AgentRun = AwaitingApproval → job ends.
/// The job payload carries (runId, brandId); all mutable state flows through Postgres.
/// Idempotent: status guard prevents re-processing a run that already reached the gate.
/// </summary>
public sealed class ExecuteRunJob
{
    private readonly AppDbContext _db;
    private readonly IBrandScope _scope;
    private readonly IBrandContext _brandContext;
    private readonly IOrchestrator _orchestrator;

    public ExecuteRunJob(
        AppDbContext db,
        IBrandScope scope,
        IBrandContext brandContext,
        IOrchestrator orchestrator)
    {
        _db = db;
        _scope = scope;
        _brandContext = brandContext;
        _orchestrator = orchestrator;
    }

    public async Task ExecuteAsync(Guid runId, Guid brandId, CancellationToken cancellationToken = default)
    {
        // Bind brand scope FIRST — every subsequent DB read is RLS-scoped to this brand.
        _brandContext.Bind(brandId);
        await using var handle = await _scope.BeginAsync(cancellationToken);

        var run = await _db.AgentRuns
            .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);

        if (run is null) return;

        // Idempotency guard: terminal or already-gated states are no-ops.
        // A run in Running may be a Hangfire retry — we re-execute the stub (deterministic).
        if (run.Status is RunStatus.AwaitingApproval
                        or RunStatus.Publishing
                        or RunStatus.Done
                        or RunStatus.Failed
                        or RunStatus.Rejected)
            return;

        var now = DateTimeOffset.UtcNow;
        run.Status = RunStatus.Running;
        run.UpdatedAt = now;

        var state = new RunState(
            RunId: runId,
            BrandId: brandId,
            Phase: GraphPhase.Strategy,
            Strategy: null,
            Creative: null,
            Caption: null,
            Media: null,
            Draft: null,
            Approval: null,
            Publish: null,
            Budget: new Budget(TokenBudget: 10_000, TokensSpent: 0, MediaBudget: 1.00m, MediaSpent: 0m),
            Errors: [],
            Trace: new TraceRefs(TraceId: string.Empty, SpanIds: []));

        // Stub orchestrator: Strategy → Creative → Copy ∥ Media → Assembly.
        // Returns RunState with Phase = AwaitingApproval.
        state = await _orchestrator.RunGenerationAsync(state, cancellationToken);

        // Checkpoint RunState → RunCheckpoint (upsert so a retry overwrites, not duplicates).
        var json = JsonSerializer.Serialize(state, RunStateJsonOptions.Options);
        var existing = await _db.RunCheckpoints
            .FirstOrDefaultAsync(c => c.AgentRunId == runId, cancellationToken);

        if (existing is not null)
        {
            existing.StateJson = json;
        }
        else
        {
            _db.RunCheckpoints.Add(new Backend.Core.Domain.RunCheckpoint
            {
                Id = Guid.NewGuid(),
                BrandId = brandId,
                AgentRunId = runId,
                StateJson = json,
                CreatedAt = now,
            });
        }

        // One-way transition: Running → AwaitingApproval.
        run.Status = RunStatus.AwaitingApproval;
        run.UpdatedAt = now;

        await _db.SaveChangesAsync(cancellationToken);
        await handle.CompleteAsync(cancellationToken);
    }
}
```

- [ ] **Step 2: Implement ResumeRunJob.cs**

Replace the entire file:

```csharp
using System.Text.Json;
using Backend.Core.Domain;
using Backend.Core.Multitenancy;
using Backend.Core.Orchestration;
using Backend.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Backend.Infrastructure.Jobs;

/// <summary>
/// Hangfire job: rehydrate RunState from RunCheckpoint → publish stub → AgentRun = Done.
/// Only runs when AgentRun.Status == AwaitingApproval; all other statuses are no-ops
/// (idempotency guard). State is read exclusively from Postgres — no in-process handoff.
/// </summary>
public sealed class ResumeRunJob
{
    private readonly AppDbContext _db;
    private readonly IBrandScope _scope;
    private readonly IBrandContext _brandContext;
    private readonly IOrchestrator _orchestrator;

    public ResumeRunJob(
        AppDbContext db,
        IBrandScope scope,
        IBrandContext brandContext,
        IOrchestrator orchestrator)
    {
        _db = db;
        _scope = scope;
        _brandContext = brandContext;
        _orchestrator = orchestrator;
    }

    public async Task ExecuteAsync(Guid runId, Guid brandId, CancellationToken cancellationToken = default)
    {
        _brandContext.Bind(brandId);
        await using var handle = await _scope.BeginAsync(cancellationToken);

        var run = await _db.AgentRuns
            .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);

        if (run is null) return;

        // Guard: only resume from Publishing. The controller transitions AwaitingApproval → Publishing
        // before enqueuing this job; re-posting to /approval returns 409 (idempotent gate).
        // Done/Failed/Rejected are terminal — a Hangfire retry after success exits cleanly.
        if (run.Status != RunStatus.Publishing) return;

        var checkpoint = await _db.RunCheckpoints
            .FirstOrDefaultAsync(c => c.AgentRunId == runId, cancellationToken);

        if (checkpoint is null) return;

        // Rehydrate from Postgres — zero reliance on prior in-process state.
        var state = JsonSerializer.Deserialize<RunState>(checkpoint.StateJson, RunStateJsonOptions.Options)!;

        // Stub publish: sets Phase = Done, PublishResult = placeholder.
        state = await _orchestrator.RunPublishAsync(state, cancellationToken);

        var now = DateTimeOffset.UtcNow;

        // Update the checkpoint with the final state (useful for trace/audit).
        checkpoint.StateJson = JsonSerializer.Serialize(state, RunStateJsonOptions.Options);

        run.Status = RunStatus.Done;
        run.UpdatedAt = now;

        await _db.SaveChangesAsync(cancellationToken);
        await handle.CompleteAsync(cancellationToken);
    }
}
```

- [ ] **Step 3: Run Durability tests to verify GREEN**

```bash
cd "C:/Users/Jamal/Documents/bootcamps/AIE/capstone/quorums/backend"
dotnet test --filter Category=Durability -v normal 2>&1 | tail -30
```

Expected: All 5 tests PASS (4 Durability + 1 Isolation).

- [ ] **Step 4: Run full test suite to verify no regressions**

```bash
dotnet test Backend.sln 2>&1 | tail -20
```

Expected: All tests pass.

- [ ] **Step 5: Commit GREEN**

```bash
git add backend/src/Infrastructure/Jobs/
git commit -m "feat(jobs): implement ExecuteRunJob + ResumeRunJob durable seam"
```

---

## Task 5: Hangfire wiring + orchestration DI

**Files:**
- Create: `backend/src/Infrastructure/Jobs/HangfireServiceCollectionExtensions.cs`
- Create: `backend/src/Infrastructure/Orchestration/OrchestrationServiceCollectionExtensions.cs`
- Modify: `backend/src/Worker/Program.cs`
- Modify: `backend/src/Api/Program.cs`

- [ ] **Step 1: Create HangfireServiceCollectionExtensions.cs**

`backend/src/Infrastructure/Jobs/HangfireServiceCollectionExtensions.cs`:

```csharp
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Infrastructure.Jobs;

/// <summary>
/// Registers the Hangfire PostgreSQL job store (used by both Api and Worker)
/// and the Hangfire server (Worker only). Both Api and Worker call
/// <see cref="AddHangfireJobStore"/>; only the Worker calls <see cref="AddHangfireWorker"/>.
/// </summary>
public static class HangfireServiceCollectionExtensions
{
    public static IServiceCollection AddHangfireJobStore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
        var schema = configuration["Hangfire:Schema"] ?? "hangfire";

        services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(
                c => c.UseConnectionString(connectionString),
                new PostgreSqlStorageOptions { SchemaName = schema }));

        // Register job classes so Hangfire's DI activator resolves them per-job.
        services.AddScoped<ExecuteRunJob>();
        services.AddScoped<ResumeRunJob>();

        return services;
    }

    /// <summary>Starts the Hangfire background-job server. Call only from the Worker host.</summary>
    public static IServiceCollection AddHangfireWorker(this IServiceCollection services)
    {
        services.AddHangfireServer();
        return services;
    }
}
```

- [ ] **Step 2: Create OrchestrationServiceCollectionExtensions.cs**

`backend/src/Infrastructure/Orchestration/OrchestrationServiceCollectionExtensions.cs`:

```csharp
using Backend.Core.Orchestration;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Infrastructure.Orchestration;

public static class OrchestrationServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IOrchestrator"/> as the stub implementation for this slice.
    /// Phase 2 replaces this registration with the real MAF-backed orchestrator.
    /// </summary>
    public static IServiceCollection AddOrchestration(this IServiceCollection services)
    {
        services.AddScoped<IOrchestrator, StubOrchestrator>();
        return services;
    }
}
```

- [ ] **Step 3: Update Worker/Program.cs**

Replace the file:

```csharp
// Worker host: Hangfire server (ExecuteRun / ResumeRun job execution), data access, orchestration.
using Backend.Infrastructure.Configuration;
using Backend.Infrastructure.Jobs;
using Backend.Infrastructure.Orchestration;
using Backend.Infrastructure.Persistence;

var builder = Host.CreateApplicationBuilder(args);

await builder.Configuration.AddVaultKvSecretsAsync();

builder.Services.AddValidatedAppOptions(builder.Configuration);
builder.Services.AddDataAccess();
builder.Services.AddHangfireJobStore(builder.Configuration);
builder.Services.AddHangfireWorker();
builder.Services.AddOrchestration();

var host = builder.Build();

host.Run();
```

- [ ] **Step 4: Update Api/Program.cs**

Add Hangfire client and orchestration registrations (after `AddOnboarding`, before `AddControllers`):

```csharp
builder.Services.AddHangfireJobStore(builder.Configuration);
builder.Services.AddOrchestration();
```

Also add the using directive at the top:

```csharp
using Backend.Infrastructure.Jobs;
using Backend.Infrastructure.Orchestration;
```

Full updated Api/Program.cs:

```csharp
using Backend.Api.Dtos;
using Backend.Api.HealthChecks;
using Backend.Api.Middleware;
using Backend.Infrastructure.Configuration;
using Backend.Infrastructure.Jobs;
using Backend.Infrastructure.Onboarding;
using Backend.Infrastructure.Orchestration;
using Backend.Infrastructure.Persistence;
using FluentValidation;
using FluentValidation.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

await builder.Configuration.AddVaultKvSecretsAsync();

builder.Services.AddValidatedAppOptions(builder.Configuration);
builder.Services.AddDataAccess();
builder.Services.AddOnboarding();
builder.Services.AddHangfireJobStore(builder.Configuration);
builder.Services.AddOrchestration();
builder.Services.AddDependencyHealthChecks(builder.Configuration);

builder.Services.AddControllers();

builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining<CreateBrandRequestValidator>();

var app = builder.Build();

app.UseMiddleware<BrandContextMiddleware>();

app.MapDependencyHealthChecks();
app.MapControllers();

app.Run();
```

- [ ] **Step 5: Build to verify**

```bash
cd "C:/Users/Jamal/Documents/bootcamps/AIE/capstone/quorums/backend"
dotnet build Backend.sln -warnaserror
```

Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add backend/src/Infrastructure/Jobs/HangfireServiceCollectionExtensions.cs \
        backend/src/Infrastructure/Orchestration/OrchestrationServiceCollectionExtensions.cs \
        backend/src/Worker/Program.cs \
        backend/src/Api/Program.cs
git commit -m "feat(infra): wire Hangfire job store + server + orchestration DI"
```

---

## Task 6: API layer (middleware + DTOs + RunsController)

**Files:**
- Create: `backend/src/Api/Middleware/BrandContextMiddleware.cs`
- Create: `backend/src/Api/Dtos/CreateRunResponse.cs`
- Create: `backend/src/Api/Dtos/ApprovalRequest.cs`
- Create: `backend/src/Api/Dtos/ApprovalRequestValidator.cs`
- Create: `backend/src/Api/Dtos/RunStatusResponse.cs`
- Create: `backend/src/Api/Controllers/RunsController.cs`

- [ ] **Step 1: Create BrandContextMiddleware.cs**

`backend/src/Api/Middleware/BrandContextMiddleware.cs`:

```csharp
using Backend.Core.Multitenancy;

namespace Backend.Api.Middleware;

/// <summary>
/// Reads the <c>X-Brand-Id</c> header and binds <see cref="IBrandContext"/> for the
/// request. This is the demo-path brand binding — in production this will be replaced
/// by reading the brand claim from the validated auth token.
/// Runs before every controller action so the RLS scope is ready before any DB access.
/// </summary>
public sealed class BrandContextMiddleware
{
    private readonly RequestDelegate _next;

    public BrandContextMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IBrandContext brandContext)
    {
        if (context.Request.Headers.TryGetValue("X-Brand-Id", out var headerValue)
            && Guid.TryParse(headerValue, out var brandId))
        {
            brandContext.Bind(brandId);
        }

        await _next(context);
    }
}
```

- [ ] **Step 2: Create DTOs**

`backend/src/Api/Dtos/CreateRunResponse.cs`:

```csharp
namespace Backend.Api.Dtos;

public sealed record CreateRunResponse(Guid RunId);
```

`backend/src/Api/Dtos/ApprovalRequest.cs`:

```csharp
namespace Backend.Api.Dtos;

/// <summary>Body for POST /runs/{id}/approval.</summary>
public sealed record ApprovalRequest(string Decision);
```

`backend/src/Api/Dtos/ApprovalRequestValidator.cs`:

```csharp
using FluentValidation;

namespace Backend.Api.Dtos;

public sealed class ApprovalRequestValidator : AbstractValidator<ApprovalRequest>
{
    private static readonly string[] ValidDecisions = ["approve", "reject"];

    public ApprovalRequestValidator()
    {
        RuleFor(r => r.Decision)
            .NotEmpty()
            .Must(d => ValidDecisions.Contains(d, StringComparer.OrdinalIgnoreCase))
            .WithMessage("Decision must be 'approve' or 'reject'.");
    }
}
```

`backend/src/Api/Dtos/RunStatusResponse.cs`:

```csharp
using Backend.Core.Domain;
using Backend.Core.Orchestration;

namespace Backend.Api.Dtos;

public sealed record RunStatusResponse(
    Guid RunId,
    RunStatus Status,
    GraphPhase? Phase);
```

- [ ] **Step 3: Create RunsController.cs**

`backend/src/Api/Controllers/RunsController.cs`:

```csharp
using Backend.Core.Domain;
using Backend.Core.Multitenancy;
using Backend.Core.Orchestration;
using Backend.Api.Dtos;
using Backend.Infrastructure.Jobs;
using Backend.Infrastructure.Persistence;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Backend.Api.Controllers;

/// <summary>
/// Run lifecycle: create a run, poll its status, and record a human approval decision.
/// Controllers are thin: validate → enqueue/persist → return. No orchestration here.
/// </summary>
[ApiController]
[Route("runs")]
public sealed class RunsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IBrandScope _scope;
    private readonly IBrandContext _brandContext;
    private readonly IBackgroundJobClient _jobs;

    public RunsController(
        AppDbContext db,
        IBrandScope scope,
        IBrandContext brandContext,
        IBackgroundJobClient jobs)
    {
        _db = db;
        _scope = scope;
        _brandContext = brandContext;
        _jobs = jobs;
    }

    /// <summary>
    /// Creates a new AgentRun for the current brand and enqueues ExecuteRun.
    /// Brand must be set via the X-Brand-Id header (bound by BrandContextMiddleware).
    /// Returns 202 Accepted with the run id.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(CreateRunResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CreateRunResponse>> Create(CancellationToken cancellationToken)
    {
        if (!_brandContext.HasBrand)
            return BadRequest(new { error = "X-Brand-Id header is required." });

        var brandId = _brandContext.RequireBrandId();

        await using var handle = await _scope.BeginAsync(cancellationToken);

        var run = new AgentRun
        {
            Id = Guid.NewGuid(),
            BrandId = brandId,
            Status = RunStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        _db.AgentRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken);
        await handle.CompleteAsync(cancellationToken);

        // Enqueue AFTER the transaction commits so the row is visible to the worker.
        _jobs.Enqueue<ExecuteRunJob>(job => job.ExecuteAsync(run.Id, brandId, CancellationToken.None));

        return Accepted($"/runs/{run.Id}", new CreateRunResponse(run.Id));
    }

    /// <summary>
    /// Returns the current status and graph phase of a run.
    /// </summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(RunStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RunStatusResponse>> Get(Guid id, CancellationToken cancellationToken)
    {
        if (!_brandContext.HasBrand)
            return BadRequest(new { error = "X-Brand-Id header is required." });

        await using var handle = await _scope.BeginAsync(cancellationToken);

        var run = await _db.AgentRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (run is null) return NotFound();

        GraphPhase? phase = null;
        var checkpoint = await _db.RunCheckpoints.AsNoTracking()
            .FirstOrDefaultAsync(c => c.AgentRunId == id, cancellationToken);

        if (checkpoint is not null)
        {
            var state = JsonSerializer.Deserialize<RunState>(
                checkpoint.StateJson, Jobs.RunStateJsonOptions.Options);
            phase = state?.Phase;
        }

        return Ok(new RunStatusResponse(run.Id, run.Status, phase));
    }

    /// <summary>
    /// Records a human approval decision. 'approve' enqueues ResumeRun (idempotent:
    /// re-posting approve when the run is no longer AwaitingApproval is a 409).
    /// 'reject' transitions to Rejected (terminal) with no resume.
    /// </summary>
    [HttpPost("{id:guid}/approval")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Approval(
        Guid id,
        ApprovalRequest request,
        CancellationToken cancellationToken)
    {
        if (!_brandContext.HasBrand)
            return BadRequest(new { error = "X-Brand-Id header is required." });

        var brandId = _brandContext.RequireBrandId();

        await using var handle = await _scope.BeginAsync(cancellationToken);

        var run = await _db.AgentRuns
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (run is null) return NotFound();

        if (run.Status != RunStatus.AwaitingApproval)
            return Conflict(new { error = $"Run is in status {run.Status} and cannot be approved/rejected." });

        var now = DateTimeOffset.UtcNow;
        var isApprove = string.Equals(request.Decision, "approve", StringComparison.OrdinalIgnoreCase);

        _db.ApprovalActions.Add(new ApprovalAction
        {
            Id = Guid.NewGuid(),
            BrandId = brandId,
            AgentRunId = id,
            Decision = isApprove ? ApprovalDecision.Approved : ApprovalDecision.Rejected,
            DecidedBy = "human",
            DecidedAt = now,
        });

        if (isApprove)
        {
            run.Status = RunStatus.Publishing;
            run.UpdatedAt = now;
            await _db.SaveChangesAsync(cancellationToken);
            await handle.CompleteAsync(cancellationToken);

            // Enqueue AFTER commit so the worker sees the updated status.
            _jobs.Enqueue<ResumeRunJob>(job => job.ExecuteAsync(id, brandId, CancellationToken.None));
        }
        else
        {
            run.Status = RunStatus.Rejected;
            run.UpdatedAt = now;
            await _db.SaveChangesAsync(cancellationToken);
            await handle.CompleteAsync(cancellationToken);
            // Terminal: no ResumeRun enqueued.
        }

        return Ok();
    }
}
```

- [ ] **Step 4: Register FluentValidation for ApprovalRequestValidator in Program.cs**

The existing `AddValidatorsFromAssemblyContaining<CreateBrandRequestValidator>()` scans the whole Api assembly, so `ApprovalRequestValidator` is auto-discovered. No changes needed.

- [ ] **Step 5: Build**

```bash
cd "C:/Users/Jamal/Documents/bootcamps/AIE/capstone/quorums/backend"
dotnet build Backend.sln -warnaserror
```

Expected: Build succeeded.

- [ ] **Step 6: Run all tests**

```bash
dotnet test Backend.sln 2>&1 | tail -20
```

Expected: All tests pass.

- [ ] **Step 7: Run dotnet format**

```bash
dotnet format Backend.sln --verify-no-changes
```

If format fails: `dotnet format Backend.sln` then re-run with `--verify-no-changes`.

- [ ] **Step 8: Commit API layer**

```bash
git add backend/src/Api/ backend/src/Infrastructure/Jobs/ResumeRunJob.cs
git commit -m "feat(api): POST /runs + POST /runs/{id}/approval + GET /runs/{id} + BrandContextMiddleware"
```

---

## Task 7: Final verification + PR

- [ ] **Step 1: Build with warnings-as-errors**

```bash
cd "C:/Users/Jamal/Documents/bootcamps/AIE/capstone/quorums/backend"
dotnet build Backend.sln -warnaserror
```

Expected: 0 warnings, 0 errors.

- [ ] **Step 2: Format gate**

```bash
dotnet format Backend.sln --verify-no-changes
```

Expected: No changes needed.

- [ ] **Step 3: Full test suite**

```bash
dotnet test Backend.sln -v normal 2>&1 | tail -30
```

Expected: All tests pass.

- [ ] **Step 4: Isolation gate**

```bash
dotnet test --filter Category=Isolation -v normal
```

Expected: All isolation tests pass (existing RLS leakage + new worker scoping).

- [ ] **Step 5: Durability gate**

```bash
dotnet test --filter Category=Durability -v normal
```

Expected: All 4 durability tests pass.

- [ ] **Step 6: Gitleaks check**

```bash
cd "C:/Users/Jamal/Documents/bootcamps/AIE/capstone/quorums"
gitleaks detect --source . --no-git 2>&1 | tail -10
```

Expected: No secrets detected.

- [ ] **Step 7: Frontend type-check (verify no regressions)**

```bash
cd "C:/Users/Jamal/Documents/bootcamps/AIE/capstone/quorums/frontend"
npx tsc --noEmit
npm run lint
```

Expected: Clean.

- [ ] **Step 8: Open PR**

```bash
cd "C:/Users/Jamal/Documents/bootcamps/AIE/capstone/quorums"
gh pr create \
  --title "[FEAT] durable run skeleton: enqueue → ExecuteRun → checkpoint → approve → ResumeRun → done" \
  --base main \
  --head feature/run-durable-skeleton \
  --body "$(cat <<'EOF'
## Summary
- Proves the durable loop thin: `POST /runs` → Hangfire `ExecuteRun` → stub orchestrator → checkpoint `RunState` at gate → job ends → approve → `ResumeRun` in a **fresh scope** → done
- Core: `RunState` record + `GraphPhase` enum + 6 typed agent-output contracts + `IOrchestrator` interface (DL-020)
- Infrastructure: `StubOrchestrator`, `ExecuteRunJob`, `ResumeRunJob` (checkpoint/resume seam), `HangfireServiceCollectionExtensions`
- Api: `POST /runs` (202), `GET /runs/{id}`, `POST /runs/{id}/approval` (approve/reject, idempotent gate)
- Worker: `AddHangfireServer` wired; same binary as Api (zero version skew)
- `BrandContextMiddleware` reads `X-Brand-Id` header for demo-path brand binding

## Out of scope (c2)
MinIO write, IMetaIntegration mock publish, Langfuse tracing, GET /runs/{id}/trace, MAF

## Test plan
- [ ] `dotnet build Backend.sln -warnaserror` green
- [ ] `dotnet format --verify-no-changes` clean
- [ ] `dotnet test` all green
- [ ] `dotnet test --filter Category=Durability` — 4 tests: checkpoint/resume proof, fresh-scope proof, idempotency proof, reject terminal proof
- [ ] `dotnet test --filter Category=Isolation` — existing RLS leakage + new worker-scoping test
- [ ] Durable resume: ExecuteRun leaves AwaitingApproval + checkpoint in DB; new DbContext ResumeRun reads checkpoint, reaches Done

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

---

## Self-review checklist

- [x] **Spec coverage**: Branch setup, RunStatus.Rejected, RunState + all typed contracts, IOrchestrator, StubOrchestrator, ExecuteRunJob, ResumeRunJob, Hangfire wiring (store + server), BrandContextMiddleware, 3 endpoints (POST /runs, GET /runs/{id}, POST /runs/{id}/approval), DTOs + FluentValidation, 5 test cases (4 Durability + 1 Isolation)
- [x] **TDD cycle**: Job stubs compiled before tests → tests RED → jobs implemented → tests GREEN
- [x] **Durable seam**: ExecuteRun ends after checkpoint; ResumeRun constructed with fresh DbContext; tests prove no in-process state crosses
- [x] **Idempotency**: ExecuteRun guard (skips if already AwaitingApproval/Done/etc.), ResumeRun guard (Publishing-only — Done/Failed/Rejected exit cleanly), approval endpoint 409 if not AwaitingApproval on re-post
- [x] **RLS**: Every DB read is inside `IBrandScope.BeginAsync()`; brand context bound from job payload (worker) or middleware (api)
- [x] **No unscoped reads**: controller loads AgentRun under scope; approval endpoint loads run under scope to get brandId
- [x] **Rejected terminal**: seeded directly in fixture; ResumeRun guard exits early; test asserts status unchanged
- [x] **ResumeRun guard is Publishing-only**: controller transitions AwaitingApproval → Publishing before enqueuing; tests call `ApproveRunAsync` fixture helper to mirror this; Rejected/Done/Failed exit early (terminal no-op)
- [x] **No MinIO/Meta/Langfuse**: StubOrchestrator has zero external calls; MediaAssetRef.StorageKey is a placeholder string
- [x] **dotnet format**: plan explicitly includes format gate before PR
- [x] **gitleaks**: plan includes gitleaks check
