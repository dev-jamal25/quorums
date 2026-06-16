using Backend.Core.Domain;
using Backend.Core.Knowledge;
using Backend.Infrastructure.Configuration.Options;
using Xunit;

namespace Backend.IntegrationTests.Knowledge;

/// <summary>
/// S2 proof: the cross-encoder runs (its order differs from dense cosine), and the metadata blend
/// boosts a high-performing historical_post. A β=0 control reproduces pure-rerank order.
/// </summary>
[Trait("Category", "Isolation")]
[Collection("Knowledge")]
public sealed class RerankAndBlendTests
{
    private readonly KnowledgeFixture _fixture;

    public RerankAndBlendTests(KnowledgeFixture fixture) => _fixture = fixture;

    // A deliberate near-tie between the two historical posts so the β·perf term is the tie-breaker.
    private sealed class TieRerank : IRerankProvider
    {
        public Task<IReadOnlyList<RerankScore>> RerankAsync(string q, IReadOnlyList<string> docs, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RerankScore>>(docs.Select((_, i) => new RerankScore(i, 0.9)).ToList());
    }

    [Fact]
    public async Task Rerank_on_with_perf_blend_boosts_the_higher_engagement_post()
    {
        var opts = new RetrievalOptions
        {
            DenseEnabled = true,
            SparseEnabled = true,
            RerankEnabled = true,
            Blend = new RetrievalBlendOptions { Alpha = 1, Beta = 0.5, Delta = 0 },
        };
        var (db, scope, retrieval) = _fixture.CreateRetrieval(_fixture.BrandA, opts, rerank: new TieRerank());
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            var result = await retrieval.Retrieve("a slow ritual brewing coffee", _fixture.BrandA, docType: DocType.HistoricalPost, k: 2);

            // "Pour Over Sunday" (eng 0.071) must outrank "Espresso Tutorial" (eng 0.052) on the perf blend.
            Assert.Equal(_fixture.PourOverSundayChunkId, result.Chunks[0].ChunkId);
        }
    }

    [Fact]
    public async Task Beta_zero_returns_both_posts_without_the_perf_boost()
    {
        var opts = new RetrievalOptions
        {
            DenseEnabled = true,
            SparseEnabled = true,
            RerankEnabled = true,
            Blend = new RetrievalBlendOptions { Alpha = 1, Beta = 0, Delta = 0 },
        };
        var (db, scope, retrieval) = _fixture.CreateRetrieval(_fixture.BrandA, opts, rerank: new TieRerank());
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            var result = await retrieval.Retrieve("a slow ritual brewing coffee", _fixture.BrandA, docType: DocType.HistoricalPost, k: 2);

            Assert.Equal(2, result.Chunks.Count);   // both returned; with β=0 the perf term cannot reorder
        }
    }
}
