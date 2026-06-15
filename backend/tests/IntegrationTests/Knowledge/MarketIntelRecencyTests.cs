using Backend.Core.Knowledge;
using Backend.Infrastructure.Configuration.Options;
using Xunit;

namespace Backend.IntegrationTests.Knowledge;

/// <summary>
/// End-to-end coverage of the S2 δ·recencyDecay arm (item 5): with the reranker held to an equal
/// relevance tie, the fresher market_intel doc must outrank the stale one.
/// </summary>
[Trait("Category", "Isolation")]
[Collection("Knowledge")]
public sealed class MarketIntelRecencyTests
{
    private readonly KnowledgeFixture _fixture;

    public MarketIntelRecencyTests(KnowledgeFixture fixture) => _fixture = fixture;

    // Equal relevance from the reranker → the δ·recencyDecay term is the sole tie-breaker.
    private sealed class TieRerank : IRerankProvider
    {
        public Task<IReadOnlyList<RerankScore>> RerankAsync(string q, IReadOnlyList<string> docs, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RerankScore>>(docs.Select((_, i) => new RerankScore(i, 0.9)).ToList());
    }

    [Fact]
    public async Task Fresher_market_intel_outranks_stale_via_recency_decay()
    {
        var opts = new RetrievalOptions
        {
            DenseEnabled = true,
            SparseEnabled = true,
            RerankEnabled = true,
            Blend = new RetrievalBlendOptions { Alpha = 1, Beta = 0, Delta = 0.5, RecencyHalfLifeDays = 365 },
        };
        var (db, scope, retrieval) = _fixture.CreateRetrieval(_fixture.BrandA, opts, rerank: new TieRerank());
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            // docType is the EF enum-name ("MarketIntel"), matching slice-2's Enum.Parse; "market_intel" would throw.
            var result = await retrieval.Retrieve("specialty single origin trend", _fixture.BrandA, docType: "MarketIntel", k: 2);

            Assert.Equal(_fixture.MarketIntelFreshChunkId, result.Chunks[0].ChunkId);   // 2026 intel beats 2024
        }
    }
}
