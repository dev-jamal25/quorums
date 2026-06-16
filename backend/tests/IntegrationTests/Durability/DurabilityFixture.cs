using System.Text.Json;
using Backend.Core.Domain;
using Backend.Core.Multitenancy;
using Backend.Core.Orchestration;
using Backend.Infrastructure.Integrations.Meta;
using Backend.Infrastructure.Jobs;
using Backend.Infrastructure.Multitenancy;
using Backend.Infrastructure.Orchestration.Maf;
using Backend.Infrastructure.Persistence;
using Backend.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Backend.IntegrationTests.Durability;

/// <summary>
/// Spins up a disposable pgvector Postgres, applies EF migrations, creates a
/// least-privilege app role (subject to RLS), and seeds two brands so the
/// durability + isolation tests can run against a real database.
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
        // Use superuser connection to bypass RLS for the approval transition,
        // mirroring a privileged controller path.
        await using var db = CreateDbContext(SuperuserConnectionString);
        var run = await db.AgentRuns.FirstAsync(r => r.Id == runId);
        var now = DateTimeOffset.UtcNow;
        run.TransitionTo(RunStatus.Publishing, now);
        db.ApprovalActions.Add(new ApprovalAction
        {
            Id = Guid.NewGuid(),
            BrandId = brandId,
            AgentRunId = runId,
            Action = ApprovalActionType.Approve,
            Actor = "test",
            OccurredAt = now,
        });
        await db.SaveChangesAsync();
    }

    public (AppDbContext Db, ExecuteRunJob Job) CreateExecuteRunJob(
        Guid brandId, GenerationAgentDeps? deps = null)
    {
        var db = CreateAppDbContext();
        var brandContext = new BrandContext();
        brandContext.Bind(brandId);
        var scope = new BrandScope(db, brandContext);
        var orchestrator = new MafOrchestrator(deps ?? TestGeneration.Deps(), new MockMetaIntegration());
        return (db, new ExecuteRunJob(db, scope, brandContext, orchestrator));
    }

    /// <summary>Reads the run's terminal status under the brand's RLS scope.</summary>
    public async Task<RunStatus?> ReadRunStatusAsync(Guid runId, Guid brandId)
    {
        var (db, scope) = CreateReadContext(brandId);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();
            var run = await db.AgentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId);
            return run?.Status;
        }
    }

    public (AppDbContext Db, ResumeRunJob Job) CreateResumeRunJob(Guid brandId)
    {
        var db = CreateAppDbContext();
        var brandContext = new BrandContext();
        brandContext.Bind(brandId);
        var scope = new BrandScope(db, brandContext);
        var orchestrator = new MafOrchestrator(
            TestGeneration.Deps(), new MockMetaIntegration());
        return (db, new ResumeRunJob(db, scope, brandContext, orchestrator));
    }

    /// <summary>
    /// Reads and deserializes the persisted <see cref="RunState"/> from the run's
    /// checkpoint, under the brand's RLS scope — the same read path the trace and
    /// status endpoints use.
    /// </summary>
    public async Task<RunState?> ReadCheckpointStateAsync(Guid runId, Guid brandId)
    {
        var (db, scope) = CreateReadContext(brandId);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();
            var checkpoint = await db.RunCheckpoints.AsNoTracking()
                .FirstOrDefaultAsync(c => c.AgentRunId == runId);
            return checkpoint is null
                ? null
                : JsonSerializer.Deserialize<RunState>(checkpoint.StateJson, RunStateJsonOptions.Options);
        }
    }

    public (AppDbContext Db, IBrandScope Scope) CreateReadContext(Guid brandId)
    {
        var db = CreateAppDbContext();
        var brandContext = new BrandContext();
        brandContext.Bind(brandId);
        return (db, new BrandScope(db, brandContext));
    }

    /// <summary>The brand-scoped trio a controller is constructed from (RLS-subject role, bound brand).</summary>
    public (AppDbContext Db, IBrandScope Scope, IBrandContext BrandContext) CreateGateDeps(Guid brandId)
    {
        var db = CreateAppDbContext();
        var brandContext = new BrandContext();
        brandContext.Bind(brandId);
        return (db, new BrandScope(db, brandContext), brandContext);
    }

    /// <summary>Seeds a run checkpoint (superuser, bypassing RLS) so a gate test can assert the draft is untouched.</summary>
    public async Task SeedCheckpointAsync(Guid runId, Guid brandId, string stateJson)
    {
        await using var db = CreateDbContext(SuperuserConnectionString);
        db.RunCheckpoints.Add(new RunCheckpoint
        {
            Id = Guid.NewGuid(),
            BrandId = brandId,
            AgentRunId = runId,
            StateJson = stateJson,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private AppDbContext CreateAppDbContext() => CreateDbContext(AppUserConnectionString);

    private static AppDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, o => o.UseVector())
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
