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
    /// readiness check per dependency. Endpoint topology is read from configuration
    /// (compose service defaults applied when a key is absent).
    ///
    /// Convention: *__Endpoint and *__Address config values store host:port only
    /// (no scheme). This method is the sole owner of the http:// prefix so the
    /// URI is never double-schemed regardless of what the env file contains.
    ///
    /// Optional dependencies (Vault) register their check only when the matching
    /// feature flag is true. A disabled dependency is not a readiness concern.
    /// </summary>
    public static IServiceCollection AddDependencyHealthChecks(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var redis = configuration["Redis:Configuration"] ?? "redis:6379";

        // host:port only — this method prepends http:// (see convention above).
        var minioEndpoint = configuration["Minio:Endpoint"] ?? "minio:9000";
        var vaultAddress = configuration["Vault:Address"] ?? "vault:8200";
        var vaultEnabled = configuration.GetValue<bool>("Vault:Enabled", false);
        var embeddingsBaseUrl = configuration["Embeddings:BaseUrl"] ?? "http://embeddings:11434";

        // Langfuse is optional: configured only when the base URL and both keys are
        // present. BaseUrl is a full URL (an API base), not a host:port endpoint.
        var langfuseBaseUrl = configuration["Langfuse:BaseUrl"];
        var langfuseConfigured =
            !string.IsNullOrWhiteSpace(langfuseBaseUrl)
            && !string.IsNullOrWhiteSpace(configuration["Langfuse:PublicKey"])
            && !string.IsNullOrWhiteSpace(configuration["Langfuse:SecretKey"]);

        var minioHealthUri = new Uri($"http://{minioEndpoint}/minio/health/live");
        var vaultHealthUri = new Uri($"http://{vaultAddress.TrimEnd('/')}/v1/sys/health");
        var embeddingsHealthUri = new Uri($"{embeddingsBaseUrl.TrimEnd('/')}/");

        var builder = services.AddHealthChecks()
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
                embeddingsHealthUri,
                name: "embeddings",
                tags: [ReadyTag]);

        // Vault is optional: only probe it when Vault:Enabled=true. A disabled
        // Vault is not a readiness concern and must not cause /health to report
        // Unhealthy for the default dev setup.
        if (vaultEnabled)
        {
            builder.AddUrlGroup(
                vaultHealthUri,
                name: "vault",
                tags: [ReadyTag]);
        }

        // Langfuse is optional in the same way: only probe it when fully configured,
        // so the default (no-op local tracing) never reports /health Unhealthy.
        if (langfuseConfigured)
        {
            var langfuseHealthUri = new Uri($"{langfuseBaseUrl!.TrimEnd('/')}/api/public/health");
            builder.AddUrlGroup(
                langfuseHealthUri,
                name: "langfuse",
                tags: [ReadyTag]);
        }

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

        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(ReadyTag),
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
