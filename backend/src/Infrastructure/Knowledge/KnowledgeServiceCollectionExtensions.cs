using Backend.Core.Knowledge;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

        return services;
    }
}
