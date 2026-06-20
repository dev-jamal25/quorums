using System.Net;
using Anthropic.SDK;
using Backend.Core.Generation;
using Backend.Core.Generation.Cost;
using Backend.Core.Generation.PlatformConstraints;
using Backend.Core.Integrations;
using Backend.Core.Knowledge;
using Backend.Core.Orchestration;
using Backend.Core.Storage;
using Backend.Infrastructure.Configuration.Options;
using Backend.Infrastructure.Integrations.Gemini;
using Backend.Infrastructure.Orchestration.Maf;
using Backend.Infrastructure.Tracing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Extensions.Http;
using Polly.Retry;
using Polly.Timeout;

namespace Backend.Infrastructure.Generation;

/// <summary>
/// Wires the generation pipeline (P2): the structured-output seam, the P1 singletons (constraints +
/// prices), the media seam, and the single <see cref="IChatClient"/> registration (chat-mode gated —
/// mock for CI/compose, live = real Anthropic; DL-032 keeps the SDK type in Infrastructure). The
/// agents share one <see cref="GenerationAgentDeps"/> bundle, scoped so its <see cref="IRetrievalService"/>
/// is the brand-scoped instance resolved inside the job's RLS scope.
/// </summary>
public static class GenerationServiceCollectionExtensions
{
    public static IServiceCollection AddGeneration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // The one Claude-call path (DL-032). mock = deterministic CI client (canned tool_use per
        // agent); live = real Anthropic. Owned here; the RAG query-transformer consumes the same client.
        // The one Claude-call path is wrapped by LangfuseChatClient so every LLM call records a Langfuse
        // generation (model + token usage) on the ambient run's trace — best-effort, no agent/node change.
        var chatMode = (configuration["Generation:ChatMode"] ?? "mock").Trim().ToLowerInvariant();
        if (chatMode == "live")
        {
            services.AddSingleton<IChatClient>(sp => new LangfuseChatClient(
                new AnthropicClient(sp.GetRequiredService<IOptions<AnthropicOptions>>().Value.ApiKey).Messages,
                sp.GetRequiredService<ITrace>()));
        }
        else
        {
            services.AddSingleton<IChatClient>(sp => new LangfuseChatClient(
                new DeterministicGenerationChatClient(),
                sp.GetRequiredService<ITrace>()));
        }

        services.AddSingleton<IStructuredGenerator, ForcedToolGenerator>();

        // P1 pure singletons, bound from the validated options.
        services.AddSingleton(sp =>
            sp.GetRequiredService<IOptions<PlatformConstraintsOptions>>().Value.ToConstraintSet());
        services.AddSingleton(sp =>
            sp.GetRequiredService<IOptions<CostPricesOptions>>().Value.ToCostPrices());

        // The in-flight Veo operation store (DL-058) is registered ALWAYS — a plain in-memory singleton
        // with no external deps, harmless for image runs. It is what makes the video async core
        // submit-or-resume idempotent (mirrors the publish-side LivePublishContextStore).
        services.AddSingleton<VeoOperationStore>();

        // Media seam (DL-001): mock = fixed image for CI/compose; live = the real Gemini image client
        // (typed HttpClient + transient/429 retry). CI runs mock only, so no live call is ever made.
        var geminiMode = (configuration["Gemini:Mode"] ?? "mock").Trim().ToLowerInvariant();
        if (geminiMode == "live")
        {
            var gemini = configuration.GetSection(GeminiOptions.SectionName).Get<GeminiOptions>() ?? new GeminiOptions();
            services
                .AddHttpClient<IMediaGenerationTool, LiveGeminiMediaTool>((sp, client) =>
                {
                    var options = sp.GetRequiredService<IOptions<GeminiOptions>>().Value;
                    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
                    client.DefaultRequestHeaders.Add("x-goog-api-key", options.ApiKey);
                    // Polly owns per-attempt timeout + retry timing; disable the ambient client timeout.
                    client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
                })
                .AddPolicyHandler(GeminiRetryPolicy(gemini.MaxRetries))        // outer: transient + 429 retry
                .AddPolicyHandler(GeminiTimeoutPolicy(gemini.TimeoutSeconds));  // inner: per-attempt timeout
        }
        else
        {
            services.AddSingleton<IMediaGenerationTool, DeterministicMediaGenerationTool>();
        }

        // Veo video seam (DL-058): the real LiveVeoClient (+ the submit-or-resume VeoVideoGenerator the
        // live media tool delegates to) is wired ONLY when Veo:Mode=live, reusing the SAME Gemini api key.
        // Absent/disabled Veo never registers a client, never crashes startup, and never breaks image runs.
        var veo = configuration.GetSection(VeoOptions.SectionName).Get<VeoOptions>() ?? new VeoOptions();
        if (veo.IsLive)
        {
            var gemini = configuration.GetSection(GeminiOptions.SectionName).Get<GeminiOptions>() ?? new GeminiOptions();
            services
                .AddHttpClient<IVeoClient, LiveVeoClient>((sp, client) =>
                {
                    var options = sp.GetRequiredService<IOptions<GeminiOptions>>().Value;
                    client.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
                    client.DefaultRequestHeaders.Add("x-goog-api-key", options.ApiKey);
                    client.Timeout = System.Threading.Timeout.InfiniteTimeSpan; // Polly owns the per-attempt timeout
                })
                .AddPolicyHandler(GeminiRetryPolicy(gemini.MaxRetries))
                .AddPolicyHandler(GeminiTimeoutPolicy(gemini.TimeoutSeconds));
            services.AddTransient<VeoVideoGenerator>();
        }

        // The agent dependency bundle (scoped — carries the scoped, brand-bound IRetrievalService).
        services.AddScoped(sp => new GenerationAgentDeps(
            Generator: sp.GetRequiredService<IStructuredGenerator>(),
            Retrieval: sp.GetRequiredService<IRetrievalService>(),
            Media: sp.GetRequiredService<IMediaGenerationTool>(),
            Storage: sp.GetRequiredService<IStorageService>(),
            Constraints: sp.GetRequiredService<PlatformConstraintSet>(),
            Prices: sp.GetRequiredService<CostPrices>(),
            GlobalCeilingUsd: sp.GetRequiredService<IOptions<CostPricesOptions>>().Value.GlobalCeilingUsd,
            SonnetModel: sp.GetRequiredService<IOptions<GenerationOptions>>().Value.SonnetModel,
            HaikuModel: sp.GetRequiredService<IOptions<GenerationOptions>>().Value.HaikuModel,
            Trace: sp.GetRequiredService<ITrace>(),
            LoggerFactory: sp.GetRequiredService<ILoggerFactory>(),
            VeoStore: sp.GetRequiredService<VeoOperationStore>(),
            VideoPricePerSec: sp.GetRequiredService<IOptions<MediaOptions>>().Value.VideoPricePerSec,
            MaxVideoDurationSec: sp.GetRequiredService<IOptions<VeoOptions>>().Value.MaxDurationSec));

        return services;
    }

    /// <summary>Honor a server <c>Retry-After</c> no longer than this; else exponential backoff.</summary>
    private static readonly TimeSpan _maxHonoredRetryAfter = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Bounded retry for the Gemini media client: transient HTTP (5xx/408/network), a per-attempt
    /// timeout, and <b>429 RESOURCE_EXHAUSTED</b> (the free-tier per-minute rate limit) — all
    /// retryable. Other 4xx (400/401/403/404) are <b>not</b> retried (fail fast). On exhaustion the
    /// non-success status surfaces, the tool throws, and the Media node maps it to a structured
    /// <c>ToolError</c> (retry-then-fail-item, DL-023).
    /// </summary>
    private static AsyncRetryPolicy<HttpResponseMessage> GeminiRetryPolicy(int maxRetries) =>
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

    private static AsyncTimeoutPolicy<HttpResponseMessage> GeminiTimeoutPolicy(int seconds) =>
        Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromSeconds(seconds));
}
