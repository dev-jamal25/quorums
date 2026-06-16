using Backend.Core.Multitenancy;
using Backend.Core.Onboarding;
using Backend.Infrastructure.Configuration.Secrets;
using Backend.Infrastructure.Multitenancy;
using Backend.Infrastructure.Onboarding;
using Backend.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Backend.IntegrationTests.Onboarding;

/// <summary>
/// Spins up a disposable pgvector Postgres, applies the EF migrations (so the RLS
/// policies are real), and creates a <b>non-superuser, non-owner</b> application
/// role. Unlike the leakage fixture it seeds NOTHING: onboarding is the code under
/// test, so it must create the brand + profile itself, through the real
/// <see cref="IBrandScope"/> binding, as the RLS-subject role.
/// </summary>
public sealed class OnboardingFixture : IAsyncLifetime
{
    private const string AppRole = "app_user";

    private readonly string _appPassword = Guid.NewGuid().ToString("N");

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg16")
        .Build();

    private string _superuserConnectionString = string.Empty;

    public string AppUserConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _superuserConnectionString = _container.GetConnectionString();

        await ApplyMigrationsAsync();
        await CreateLeastPrivilegeRoleAsync();
        AppUserConnectionString = BuildAppUserConnectionString();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>A context on the RLS-subject role with no brand bound.</summary>
    public AppDbContext CreateAppContext() => CreateDbContext(AppUserConnectionString);

    /// <summary>The RLS-subject role plus the real <see cref="IBrandScope"/> bound to a brand.</summary>
    public (AppDbContext Db, IBrandScope Scope) CreateBrandScopedContext(Guid brandId)
    {
        var db = CreateDbContext(AppUserConnectionString);
        var brandContext = new BrandContext();
        brandContext.Bind(brandId);
        return (db, new BrandScope(db, brandContext));
    }

    /// <summary>
    /// The production onboarding path on the RLS-subject role: a fresh, UNBOUND
    /// <see cref="BrandContext"/> handed to the service, which binds it to the brand
    /// id it generates. Returns the context so the caller disposes it.
    /// </summary>
    public (AppDbContext Db, IBrandOnboardingService Service) CreateOnboardingService()
    {
        var db = CreateDbContext(AppUserConnectionString);
        var brandContext = new BrandContext();
        var scope = new BrandScope(db, brandContext);
        return (db, new BrandOnboardingService(db, brandContext, scope, new PassthroughSecretsProvider()));
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
        await using var context = CreateDbContext(_superuserConnectionString);
        await context.Database.MigrateAsync();
    }

    private async Task CreateLeastPrivilegeRoleAsync()
    {
        await using var admin = CreateDbContext(_superuserConnectionString);

        // DDL cannot be parameterized; the only interpolated values are a constant
        // role name and a per-run random password (never external input).
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
        new NpgsqlConnectionStringBuilder(_superuserConnectionString)
        {
            Username = AppRole,
            Password = _appPassword,
        }.ConnectionString;
}
