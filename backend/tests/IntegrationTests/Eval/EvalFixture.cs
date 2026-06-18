using Backend.Core.Multitenancy;
using Backend.Infrastructure.Evaluation;
using Backend.Infrastructure.Multitenancy;
using Backend.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Backend.IntegrationTests.Eval;

/// <summary>
/// Spins up a disposable pgvector Postgres, applies the EF migrations (so the eval_runs/eval_results RLS
/// policies are real), creates a non-superuser RLS-subject role, and exposes a brand-scoped
/// <see cref="EvalRunPersistence"/> plus a temp disk-store root. Mirrors the RLS leakage fixture — the
/// eval harness dual-writes through the production brand-scope binding, never a test bypass.
/// </summary>
public sealed class EvalFixture : IAsyncLifetime
{
    private const string AppRole = "app_user";
    private readonly string _appPassword = Guid.NewGuid().ToString("N");

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg16")
        .Build();

    public string SuperuserConnectionString { get; private set; } = string.Empty;

    public string AppUserConnectionString { get; private set; } = string.Empty;

    public Guid BrandId { get; } = Guid.NewGuid();

    /// <summary>
    /// The library disk result store + response cache root (what `dotnet aieval` reads). Honors
    /// <c>EVAL_REPORT_STORE</c> so a verification run can point it at a stable repo path to then run
    /// `dotnet aieval report`; otherwise a unique temp dir that is cleaned up on dispose.
    /// </summary>
    public string StorageRoot { get; } =
        Environment.GetEnvironmentVariable(EvalReportingFactory.StorageRootEnvVar) is { Length: > 0 } configured
            ? configured
            : Path.Combine(Path.GetTempPath(), "quorums-eval-test-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        SuperuserConnectionString = _container.GetConnectionString();

        await ApplyMigrationsAsync();
        await CreateLeastPrivilegeRoleAsync();
        AppUserConnectionString = BuildAppUserConnectionString();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();

        // Only clean up a temp store; a path provided via EVAL_REPORT_STORE is left for `dotnet aieval`.
        var isTempStore = StorageRoot.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase);
        if (!isTempStore)
        {
            return;
        }

        try
        {
            if (Directory.Exists(StorageRoot))
            {
                Directory.Delete(StorageRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp report store.
        }
    }

    /// <summary>A brand-scoped <see cref="EvalRunPersistence"/> (the production write path) for the harness.</summary>
    public (EvalRunPersistence Persistence, AppDbContext Db) CreatePersistence()
    {
        var db = CreateDbContext(AppUserConnectionString);
        var brandContext = new BrandContext();
        brandContext.Bind(BrandId);
        return (new EvalRunPersistence(db, new BrandScope(db, brandContext)), db);
    }

    /// <summary>A brand-scoped read context for verifying the persisted rows.</summary>
    public (AppDbContext Db, IBrandScope Scope) CreateBrandScopedContext()
    {
        var db = CreateDbContext(AppUserConnectionString);
        var brandContext = new BrandContext();
        brandContext.Bind(BrandId);
        return (db, new BrandScope(db, brandContext));
    }

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
}
