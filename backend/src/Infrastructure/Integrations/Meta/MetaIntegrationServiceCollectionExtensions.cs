using System.Net;
using Backend.Core.Integrations;
using Backend.Infrastructure.Configuration.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;
using Polly.Retry;
using Polly.Timeout;

namespace Backend.Infrastructure.Integrations.Meta;

public static class MetaIntegrationServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IMetaIntegration"/> by mode (DL-055). The default <c>Meta:Mode=mock</c>
    /// selects the network-free mock (Singleton — stateless); <c>live</c> selects the real
    /// <see cref="LiveMetaIntegration"/> as a typed <see cref="HttpClient"/> with the Graph base address
    /// and a Polly transient/429-retry + per-attempt-timeout policy. CI/default never resolves the live
    /// client, so no live Meta call is ever made. The brand-connection seeding seam
    /// (<see cref="BrandMetaConnector"/>) is registered in both modes.
    /// </summary>
    public static IServiceCollection AddMetaIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        // Brand-scoped seeding seam for the `meta-connect` CLI (Transit-encrypts the token, upserts the
        // BrandMetaConnection under RLS). Registered regardless of mode.
        services.AddScoped<BrandMetaConnector>();

        var mode = (configuration[$"{MetaOptions.SectionName}:Mode"] ?? "mock").Trim().ToLowerInvariant();
        if (mode == "live")
        {
            // Singleton so the per-creation publish context survives the transient typed-client instances
            // a Hangfire retry resolves (DL-055 live recovery seam).
            services.AddSingleton<LivePublishContextStore>();

            var options = configuration.GetSection(MetaOptions.SectionName).Get<MetaOptions>() ?? new MetaOptions { Mode = "live" };
            services
                .AddHttpClient<IMetaIntegration, LiveMetaIntegration>((sp, client) =>
                {
                    var meta = sp.GetRequiredService<IOptions<MetaOptions>>().Value;
                    client.BaseAddress = new Uri(meta.GraphBaseUrl.TrimEnd('/') + "/");
                    // Polly owns the per-attempt timeout; disable the ambient client timeout.
                    client.Timeout = Timeout.InfiniteTimeSpan;
                })
                .AddPolicyHandler(RetryPolicy(options.MaxRetries))       // outer: transient + 429 retry
                .AddPolicyHandler(TimeoutPolicy(options.TimeoutSeconds)); // inner: per-attempt timeout
        }
        else if (mode == "mock")
        {
            services.AddSingleton<IMetaIntegration>(new MockMetaIntegration());
        }
        else
        {
            throw new InvalidOperationException($"Unknown Meta:Mode '{mode}'. Expected 'mock' or 'live'.");
        }

        return services;
    }

    /// <summary>Honor a server <c>Retry-After</c> no longer than this; else exponential backoff.</summary>
    private static readonly TimeSpan _maxHonoredRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Bounded retry for the live Graph client: transient HTTP (5xx/408/network), a per-attempt timeout,
    /// and 429 (rate limit) — all retryable in-call. Other 4xx (auth/policy/invalid) are NOT retried;
    /// they surface as a terminal <c>PublishStatus</c> for the reviewer.
    /// </summary>
    private static AsyncRetryPolicy<HttpResponseMessage> RetryPolicy(int maxRetries) =>
        HttpPolicyExtensions
            .HandleTransientHttpError()
            .OrResult(response => response.StatusCode == HttpStatusCode.TooManyRequests)
            .Or<TimeoutRejectedException>()
            .WaitAndRetryAsync(
                maxRetries,
                sleepDurationProvider: (attempt, outcome, _) => RetryDelay(attempt, outcome),
                onRetryAsync: (_, _, _, _) => Task.CompletedTask);

    private static TimeSpan RetryDelay(int attempt, DelegateResult<HttpResponseMessage> outcome)
    {
        var retryAfter = outcome.Result?.Headers.RetryAfter;
        var honored = retryAfter?.Delta
            ?? (retryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (honored is { } delay && delay > TimeSpan.Zero)
        {
            return delay < _maxHonoredRetryAfter ? delay : _maxHonoredRetryAfter;
        }

        return TimeSpan.FromSeconds(Math.Pow(2, attempt));
    }

    private static AsyncTimeoutPolicy<HttpResponseMessage> TimeoutPolicy(int seconds) =>
        Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromSeconds(seconds));
}
