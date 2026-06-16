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
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
        var chatMode = (configuration["Generation:ChatMode"] ?? "mock").Trim().ToLowerInvariant();
        if (chatMode == "live")
        {
            services.AddSingleton<IChatClient>(sp =>
                new AnthropicClient(sp.GetRequiredService<IOptions<AnthropicOptions>>().Value.ApiKey).Messages);
        }
        else
        {
            services.AddSingleton<IChatClient>(_ => new DeterministicGenerationChatClient());
        }

        services.AddSingleton<IStructuredGenerator, ForcedToolGenerator>();

        // P1 pure singletons, bound from the validated options.
        services.AddSingleton(sp =>
            sp.GetRequiredService<IOptions<PlatformConstraintsOptions>>().Value.ToConstraintSet());
        services.AddSingleton(sp =>
            sp.GetRequiredService<IOptions<CostPricesOptions>>().Value.ToCostPrices());

        // Media seam (DL-001): mock = fixed image for CI/compose; live throws until the real Gemini step (P3).
        var geminiMode = (configuration["Gemini:Mode"] ?? "mock").Trim().ToLowerInvariant();
        if (geminiMode == "live")
        {
            services.AddSingleton<IMediaGenerationTool, LiveGeminiMediaTool>();
        }
        else
        {
            services.AddSingleton<IMediaGenerationTool, DeterministicMediaGenerationTool>();
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
            LoggerFactory: sp.GetRequiredService<ILoggerFactory>()));

        return services;
    }
}
