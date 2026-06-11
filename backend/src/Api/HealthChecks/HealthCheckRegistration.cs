using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Backend.Api.HealthChecks;

/// <summary>
/// Wires the GET /health surface: a process liveness check plus async readiness
/// checks for every external dependency (postgres, redis, minio, vault,
/// embeddings). Dependency clients are not constructed here — each check is the
/// framework's async probe configured from the bound configuration. No business
/// logic lives in this seam.
/// </summary>
public static class HealthCheckRegistration
{
    private const string LiveTag = "live";
    private const string ReadyTag = "ready";

    /// <summary>
    /// Registers the health check services in DI: a self/liveness check and one
    /// readiness check per dependency. Endpoint topology is read from
    /// configuration (compose service defaults applied when a key is absent);
    /// the Postgres connection string is resolved at check time so a missing
    /// secret surfaces as an unhealthy entry rather than a startup crash.
    /// </summary>
    public static IServiceCollection AddDependencyHealthChecks(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var redis = configuration["Redis:Configuration"] ?? "redis:6379";
        var minioEndpoint = configuration["Minio:Endpoint"] ?? "minio:9000";
        var vaultAddress = configuration["Vault:Address"] ?? "http://vault:8200";
        var embeddingsBaseUrl = configuration["Embeddings:BaseUrl"] ?? "http://embeddings:11434";

        var minioHealthUri = new Uri($"http://{minioEndpoint}/minio/health/live");
        var vaultHealthUri = new Uri($"{vaultAddress.TrimEnd('/')}/v1/sys/health");
        var embeddingsHealthUri = new Uri($"{embeddingsBaseUrl.TrimEnd('/')}/");

        services.AddHealthChecks()
            // Liveness: the process is up and serving. Always healthy if reached.
            .AddCheck("self", () => HealthCheckResult.Healthy("Process is live."), tags: [LiveTag])
            // Readiness: each external dependency, probed asynchronously.
            .AddNpgSql(
                sp => sp.GetRequiredService<IConfiguration>().GetConnectionString("Postgres")
                    ?? string.Empty,
                name: "postgres",
                tags: [ReadyTag])
            .AddRedis(
                redis,
                name: "redis",
                tags: [ReadyTag])
            .AddUrlGroup(
                minioHealthUri,
                name: "minio",
                tags: [ReadyTag])
            .AddUrlGroup(
                vaultHealthUri,
                name: "vault",
                tags: [ReadyTag])
            .AddUrlGroup(
                embeddingsHealthUri,
                name: "embeddings",
                tags: [ReadyTag]);

        return services;
    }

    /// <summary>
    /// Maps GET /health (aggregated liveness + all dependency checks) and
    /// GET /health/live (liveness only). Both emit an aggregated status with
    /// per-dependency detail; raw exceptions are never written to the response.
    /// </summary>
    public static IEndpointRouteBuilder MapDependencyHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = WriteAggregatedResponseAsync,
        });

        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(LiveTag),
            ResponseWriter = WriteAggregatedResponseAsync,
        });

        return endpoints;
    }

    private static Task WriteAggregatedResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var payload = new
        {
            status = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            results = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new
                {
                    status = entry.Value.Status.ToString(),
                    description = entry.Value.Description,
                    durationMs = entry.Value.Duration.TotalMilliseconds,
                    // Deliberately omit Exception detail — never leak internals/secrets.
                }),
        };

        return context.Response.WriteAsJsonAsync(payload);
    }
}
