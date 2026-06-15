using Anthropic.SDK;
using Backend.Core.Knowledge;
using Backend.Infrastructure.Configuration.Options;
using Backend.Infrastructure.Knowledge.Seed;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Backend.Infrastructure.Knowledge;

/// <summary>
/// Registers the brand-knowledge RAG services: the type-dispatched chunker, the ingest
/// service, and the embedding provider. The embedder is config-gated by
/// <c>Embeddings:Mode</c> (<c>nomic</c> = real tei-embed, <c>mock</c> = deterministic
/// offline). Retrieval + its options are added by the retrieval slice.
/// </summary>
public static class KnowledgeServiceCollectionExtensions
{
    public static IServiceCollection AddKnowledge(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<IKnowledgeChunker, TypeDispatchedChunker>();
        services.AddScoped<IKnowledgeIngestService, KnowledgeIngestService>();
        services.AddScoped<IRetrievalService, PgVectorRetrieval>();
        services.AddScoped<KnowledgeSeeder>();

        var mode = (configuration["Embeddings:Mode"] ?? "nomic").Trim().ToLowerInvariant();
        if (mode == "mock")
        {
            services.AddSingleton<IEmbeddingProvider, DeterministicEmbeddingProvider>();
        }
        else
        {
            // Endpoint stores host:port only; the app prepends the scheme (scaffold convention).
            var endpoint = configuration["Embeddings:Endpoint"] ?? "tei-embed:80";
            services.AddHttpClient<IEmbeddingProvider, NomicEmbeddingProvider>(client =>
            {
                client.BaseAddress = new Uri($"http://{endpoint}");
                client.Timeout = TimeSpan.FromSeconds(30);
            });
        }

        // S2 reranker (DL-024/025), config-gated by Reranker:Mode (mirrors Embeddings:Mode).
        var rerankMode = (configuration["Reranker:Mode"] ?? "tei").Trim().ToLowerInvariant();
        if (rerankMode == "mock")
        {
            services.AddSingleton<IRerankProvider, DeterministicRerankProvider>();
        }
        else
        {
            var rerankEndpoint = configuration["Reranker:Endpoint"] ?? "tei-rerank:80";
            services.AddHttpClient<IRerankProvider, CrossEncoderRerankProvider>(client =>
            {
                client.BaseAddress = new Uri($"http://{rerankEndpoint}");
                client.Timeout = TimeSpan.FromSeconds(30);
            });
        }

        // S0 query transformer (DL-025), config-gated by QueryTransform:Mode (CI uses mock).
        var qtMode = (configuration["QueryTransform:Mode"] ?? "chat").Trim().ToLowerInvariant();
        if (qtMode == "mock")
        {
            services.AddSingleton<IQueryTransformer, DeterministicQueryTransformer>();
        }
        else
        {
            // The single Claude-call path (item 6): an Anthropic-backed Microsoft.Extensions.AI
            // IChatClient. ApiKey from AnthropicOptions (Vault/secret); the model id is config-bound
            // on the call (ChatOptions.ModelId). AnthropicClient.Messages implements IChatClient.
            services.AddSingleton<IChatClient>(sp =>
            {
                var apiKey = sp.GetRequiredService<IOptions<AnthropicOptions>>().Value.ApiKey;
                return new AnthropicClient(apiKey).Messages;
            });
            services.AddSingleton<IQueryTransformer, ChatQueryTransformer>();
        }

        return services;
    }
}
