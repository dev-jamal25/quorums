using Backend.Infrastructure.Configuration.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Infrastructure.Configuration;

/// <summary>
/// Registers every app-config section as a strongly-typed <c>IOptions&lt;T&gt;</c>
/// with data-annotation validation that runs at startup. A missing or invalid
/// required key makes the host refuse to boot (fail fast, DL-011) rather than
/// surfacing as a null at first use. Consumed via constructor-injected
/// <c>IOptions&lt;T&gt;</c>; no component re-reads configuration directly.
/// </summary>
public static class OptionsServiceCollectionExtensions
{
    public static IServiceCollection AddValidatedAppOptions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddValidatedOptions<DatabaseOptions>(configuration, DatabaseOptions.SectionName);
        services.AddValidatedOptions<VaultOptions>(configuration, VaultOptions.SectionName);
        services.AddValidatedOptions<AnthropicOptions>(configuration, AnthropicOptions.SectionName);
        services.AddValidatedOptions<GeminiOptions>(configuration, GeminiOptions.SectionName);
        services.AddValidatedOptions<MinioOptions>(configuration, MinioOptions.SectionName);
        // Non-secret public asset origin for the live Meta MediaUrl (DL-055); optional (empty in mock/CI).
        services.AddValidatedOptions<StorageOptions>(configuration, StorageOptions.SectionName);
        services.AddValidatedOptions<RedisOptions>(configuration, RedisOptions.SectionName);
        services.AddValidatedOptions<EmbeddingsOptions>(configuration, EmbeddingsOptions.SectionName);
        services.AddValidatedOptions<RetrievalOptions>(configuration, RetrievalOptions.SectionName);
        services.AddValidatedOptions<RerankerOptions>(configuration, RerankerOptions.SectionName);
        services.AddValidatedOptions<QueryTransformOptions>(configuration, QueryTransformOptions.SectionName);
        services.AddValidatedOptions<HangfireOptions>(configuration, HangfireOptions.SectionName);
        services.AddValidatedOptions<MetaOptions>(configuration, MetaOptions.SectionName);
        services.AddValidatedOptions<RegenerationOptions>(configuration, RegenerationOptions.SectionName);
        // Generation pipeline (DL-027/029/030): model selection, cost prices, platform constraints
        // — all config-bound, never literals in agent code.
        services.AddValidatedOptions<GenerationOptions>(configuration, GenerationOptions.SectionName);
        services.AddValidatedOptions<CostPricesOptions>(configuration, CostPricesOptions.SectionName);
        services.AddValidatedOptions<PlatformConstraintsOptions>(configuration, PlatformConstraintsOptions.SectionName);
        // Veo video generation (DL-058): all optional-with-defaults, so an absent Veo/Media section
        // never crashes startup or breaks image runs (the image path reads neither).
        services.AddValidatedOptions<VeoOptions>(configuration, VeoOptions.SectionName);
        services.AddValidatedOptions<MediaOptions>(configuration, MediaOptions.SectionName);
        // Phase-9 LLM-judge tier (DL-057): the config-bound pass threshold for the κ gate.
        services.AddValidatedOptions<JudgeOptions>(configuration, JudgeOptions.SectionName);
        // Langfuse keys are optional (empty = no-op local tracing); no [Required], so
        // validation is a no-op but the binding stays consistent with every other section.
        services.AddValidatedOptions<LangfuseOptions>(configuration, LangfuseOptions.SectionName);
        return services;
    }

    private static void AddValidatedOptions<TOptions>(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName)
        where TOptions : class
    {
        services.AddOptions<TOptions>()
            .Bind(configuration.GetSection(sectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
    }
}
