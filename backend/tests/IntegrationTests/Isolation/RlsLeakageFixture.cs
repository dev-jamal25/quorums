using Backend.Core.Domain;
using Backend.Core.Multitenancy;
using Backend.Infrastructure.Multitenancy;
using Backend.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Backend.IntegrationTests.Isolation;

/// <summary>
/// Spins up a disposable pgvector Postgres, applies the EF migrations (so the RLS
/// policies authored via <c>migrationBuilder.Sql</c> are real), creates a
/// <b>non-superuser, non-owner</b> application role, and seeds two brands.
/// </summary>
/// <remarks>
/// The least-privilege role is the crux of a meaningful test: Postgres superusers
/// (and table owners, absent FORCE) bypass RLS, so a test that connected with the
/// container's default <c>postgres</c> superuser would prove nothing. The app path
/// connects as this RLS-subject role; migrations and seeding use the superuser.
/// </remarks>
public sealed class RlsLeakageFixture : IAsyncLifetime
{
    private const string AppRole = "app_user";

    // Generated per run — no credential literal lives in the repo.
    private readonly string _appPassword = Guid.NewGuid().ToString("N");

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg16")
        .Build();

    public string SuperuserConnectionString { get; private set; } = string.Empty;

    public string AppUserConnectionString { get; private set; } = string.Empty;

    public Guid BrandA { get; } = Guid.NewGuid();

    public Guid BrandB { get; } = Guid.NewGuid();

    public Guid ProfileA { get; } = Guid.NewGuid();

    public Guid ProfileB { get; } = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        SuperuserConnectionString = _container.GetConnectionString();

        await ApplyMigrationsAsync();
        await CreateLeastPrivilegeRoleAsync();
        AppUserConnectionString = BuildAppUserConnectionString();
        await SeedTwoBrandsAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>A context on the RLS-subject role with no brand bound.</summary>
    public AppDbContext CreateAppContext() => CreateDbContext(AppUserConnectionString);

    /// <summary>
    /// A context on the RLS-subject role plus the real <see cref="IBrandScope"/>
    /// bound to <paramref name="brandId"/>. This is the production binding path, not
    /// a test bypass: <c>scope.BeginAsync()</c> opens the work transaction and issues
    /// the transaction-local <c>set_config</c> as its first statement.
    /// </summary>
    public (AppDbContext Db, IBrandScope Scope) CreateBrandScopedContext(Guid brandId)
    {
        var db = CreateDbContext(AppUserConnectionString);
        var brandContext = new BrandContext();
        brandContext.Bind(brandId);
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

        // DDL cannot be parameterized; the only interpolated values are a constant
        // role name and a per-run random password (never external input). Built into
        // a local so the raw-SQL call takes a plain string.
        // NOSUPERUSER + NOBYPASSRLS + non-owner = fully subject to the policies.
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

    private async Task SeedTwoBrandsAsync()
    {
        // Seeded through the superuser, which bypasses RLS — so rows for both brands
        // exist regardless of any brand binding. The app role can then only ever see
        // its own.
        await using var seed = CreateDbContext(SuperuserConnectionString);
        var now = DateTimeOffset.UtcNow;

        seed.Brands.AddRange(
            new Brand { Id = BrandA, Name = "Brand A", CreatedAt = now },
            new Brand { Id = BrandB, Name = "Brand B", CreatedAt = now });

        seed.BrandProfiles.AddRange(
            new BrandProfile
            {
                Id = ProfileA,
                BrandId = BrandA,
                Positioning = "Brand A positioning",
                ToneDescriptors = ["warm"],
                ImageryStyle = "soft",
                ProductContext = "Brand A products",
                CreatedAt = now,
            },
            new BrandProfile
            {
                Id = ProfileB,
                BrandId = BrandB,
                Positioning = "Brand B positioning",
                ToneDescriptors = ["bold"],
                ImageryStyle = "stark",
                ProductContext = "Brand B products",
                CreatedAt = now,
            });

        seed.AgentRuns.AddRange(
            new AgentRun { Id = Guid.NewGuid(), BrandId = BrandA, Status = RunStatus.Queued, CreatedAt = now, UpdatedAt = now },
            new AgentRun { Id = Guid.NewGuid(), BrandId = BrandB, Status = RunStatus.Queued, CreatedAt = now, UpdatedAt = now });

        await seed.SaveChangesAsync();
    }
}
