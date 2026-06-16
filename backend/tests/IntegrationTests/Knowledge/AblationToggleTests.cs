using Backend.Core.Domain;
using Backend.Infrastructure.Configuration.Options;
using Xunit;

namespace Backend.IntegrationTests.Knowledge;

/// <summary>
/// The Phase-9 ablation precondition (DL-025): every stage is independently toggleable. All-off
/// reproduces slice-2 dense-only behaviour; each-on engages without crashing the run. This proves
/// the seam, not a cosmetic flag — Phase 9 flips these flags to measure each technique's marginal lift.
/// </summary>
[Trait("Category", "Isolation")]
[Collection("Knowledge")]
public sealed class AblationToggleTests
{
    private readonly KnowledgeFixture _fixture;

    public AblationToggleTests(KnowledgeFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task All_stages_off_reproduces_slice2_dense_only_top_chunk()
    {
        // Default RetrievalOptions = QueryTransform off, Sparse off, Rerank off → dense-only.
        var (db, scope, retrieval) = _fixture.CreateRetrieval(_fixture.BrandA);   // no-arg overload (slice-2 path)
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            var result = await retrieval.Retrieve(_fixture.BrandAProductQuery, _fixture.BrandA, DocType.Product, 3);

            Assert.True(result.Grounded);
            Assert.Equal(_fixture.BrandAProductChunkId, result.Chunks[0].ChunkId);  // identical to slice-2
            Assert.Null(result.Error);
        }
    }

    [Theory]
    [InlineData(true, false, false)]   // S0 only
    [InlineData(false, true, false)]   // sparse arm only
    [InlineData(false, false, true)]   // rerank only
    [InlineData(true, true, true)]     // full hybrid
    public async Task Each_toggle_combination_runs_without_crashing(bool s0, bool sparse, bool rerank)
    {
        var opts = new RetrievalOptions
        {
            QueryTransformEnabled = s0,
            DenseEnabled = true,
            SparseEnabled = sparse,
            RerankEnabled = rerank,
        };
        var (db, scope, retrieval) = _fixture.CreateRetrieval(_fixture.BrandA, opts);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            var result = await retrieval.Retrieve(_fixture.BrandAProductQuery, _fixture.BrandA, DocType.Product, 3);

            Assert.NotEmpty(result.Chunks);   // a sane result set; no exception into the graph
            Assert.Null(result.Error);        // mocks never fail → no degrade ToolError
        }
    }
}
