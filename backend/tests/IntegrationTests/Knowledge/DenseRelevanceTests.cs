using Backend.Infrastructure.Configuration.Options;
using Backend.Infrastructure.Knowledge;
using Backend.Infrastructure.Knowledge.Seed;
using Microsoft.Extensions.Options;
using Xunit;

namespace Backend.IntegrationTests.Knowledge;

/// <summary>
/// Dense relevance (DL-025): a seeded query returns the right chunk. The mock-based test
/// runs the full retrieval path in CI (the deterministic embedder makes shared vocabulary
/// nearest); the live test proves the same ranking against a real tei-embed and is opt-in.
/// </summary>
[Trait("Category", "Isolation")]
[Collection("Knowledge")]
public sealed class DenseRelevanceTests
{
    private readonly KnowledgeFixture _fixture;

    public DenseRelevanceTests(KnowledgeFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Seeded_query_returns_the_expected_product_chunk_first()
    {
        var (db, scope, retrieval) = _fixture.CreateRetrieval(_fixture.BrandA);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            var result = await retrieval.Retrieve(
                _fixture.BrandAProductQuery, _fixture.BrandA, docType: "product", k: 3);

            Assert.True(result.Grounded);
            Assert.Contains(_fixture.BrandAProductChunkId, result.Chunks.Select(c => c.ChunkId));
            Assert.Equal(_fixture.BrandAProductChunkId, result.Chunks[0].ChunkId); // nearest
        }
    }

    // Opt-in: requires a running tei-embed (Embeddings:Mode=nomic). Remove Skip to run locally
    // with Embeddings__Endpoint set (e.g. localhost:8080). Excluded from CI — CI never calls TEI.
    [Fact(Skip = "Opt-in live test: requires a running tei-embed. Remove Skip to run locally.")]
    [Trait("Category", "LiveEmbeddings")]
    public async Task Real_tei_embed_ranks_matching_product_nearer_than_unrelated()
    {
        var endpoint = Environment.GetEnvironmentVariable("Embeddings__Endpoint") ?? "localhost:8080";
        using var http = new HttpClient { BaseAddress = new Uri($"http://{endpoint}") };
        var provider = new NomicEmbeddingProvider(
            http,
            Options.Create(new EmbeddingsOptions { BaseUrl = endpoint, Model = "nomic-embed-text-v1.5", Dimension = 768 }));

        var query = await provider.EmbedQueryAsync(CoffeeRoasterCorpus.RelevanceQuery);
        var match = await provider.EmbedDocumentAsync(
            "Ethiopia Yirgacheffe single origin. Floral jasmine and bergamot, bright citrus, light roast.");
        var unrelated = await provider.EmbedDocumentAsync(
            "Sunrise espresso blend. Chocolate, caramel, toasted nut, medium-dark roast.");

        Assert.True(Cosine(query, match) > Cosine(query, unrelated));
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0;
        double na = 0;
        double nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
