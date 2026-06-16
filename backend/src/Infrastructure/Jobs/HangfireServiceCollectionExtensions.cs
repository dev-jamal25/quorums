using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Infrastructure.Jobs;

public static class HangfireServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared Hangfire PostgreSQL job store. <paramref name="installSchema"/> makes
    /// schema install SINGLE-AUTHORITY: only the Worker installs (runs <c>PostgreSqlObjectsInstaller</c>,
    /// i.e. <c>CREATE SCHEMA "hangfire"</c> + tables); the Api uses-but-does-not-install. Two processes
    /// installing concurrently race on <c>pg_namespace_nspname_index</c> on a cold/fresh database and one
    /// crashes — the Api waits on the Worker's schema-readiness healthcheck instead (see docker-compose).
    /// </summary>
    public static IServiceCollection AddHangfireJobStore(
        this IServiceCollection services,
        IConfiguration configuration,
        bool installSchema)
    {
        var connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
        var schema = configuration["Hangfire:Schema"] ?? "hangfire";

        services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(opts => opts.UseNpgsqlConnection(connectionString),
                new PostgreSqlStorageOptions { SchemaName = schema, PrepareSchemaIfNecessary = installSchema }));

        services.AddScoped<ExecuteRunJob>();
        services.AddScoped<ResumeRunJob>();
        services.AddScoped<RegenerateRunJob>();
        services.AddScoped<IngestKnowledgeDocJob>();

        return services;
    }

    public static IServiceCollection AddHangfireWorker(this IServiceCollection services)
    {
        services.AddHangfireServer();
        return services;
    }
}
