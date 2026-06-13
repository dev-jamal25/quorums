using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Infrastructure.Jobs;

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
            .UsePostgreSqlStorage(opts => opts.UseNpgsqlConnection(connectionString),
                new PostgreSqlStorageOptions { SchemaName = schema }));

        services.AddScoped<ExecuteRunJob>();
        services.AddScoped<ResumeRunJob>();

        return services;
    }

    public static IServiceCollection AddHangfireWorker(this IServiceCollection services)
    {
        services.AddHangfireServer();
        return services;
    }
}
