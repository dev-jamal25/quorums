using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Backend.IntegrationTests.Knowledge;

/// <summary>
/// The bar (DL-010): two brands with identical corpus, separated only by RLS. Brand A's
/// dense retrieval returns zero Brand B chunks, and vice versa. Ownership is verified via a
/// superuser context (bypasses RLS) so a broken policy cannot hide; the query uses terms
/// present in BOTH brands, and a guard asserts the other brand really has matching chunks,
/// so the result can never pass vacuously.
/// </summary>
[Trait("Category", "Isolation")]
public sealed class RagIsolationTests : IClassFixture<KnowledgeFixture>
{
    private const string SharedQuery = "brand voice and roast style";

    private readonly KnowledgeFixture _fixture;

    public RagIsolationTests(KnowledgeFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Brand_A_retrieval_returns_zero_brand_B_chunks()
    {
        var (db, scope, retrieval) = _fixture.CreateRetrieval(_fixture.BrandA);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            var result = await retrieval.Retrieve(SharedQuery, _fixture.BrandA, docType: null, k: 20);
            Assert.NotEmpty(result.Chunks);

            await using var admin = _fixture.CreateSuperuserContext(); // bypasses RLS — sees all brands

            // Guard against a vacuous pass: Brand B really has chunks (identical corpus), so
            // only RLS — not an empty corpus — keeps them out of A's result.
            Assert.True(await admin.KnowledgeChunks.AsNoTracking().CountAsync(c => c.BrandId == _fixture.BrandB) > 0);

            foreach (var hit in result.Chunks)
            {
                var owner = await admin.KnowledgeChunks.AsNoTracking()
                    .Where(c => c.Id == hit.ChunkId).Select(c => c.BrandId).FirstAsync();
                Assert.Equal(_fixture.BrandA, owner);
            }
        }
    }

    [Fact]
    public async Task Brand_B_retrieval_returns_zero_brand_A_chunks()
    {
        var (db, scope, retrieval) = _fixture.CreateRetrieval(_fixture.BrandB);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            var result = await retrieval.Retrieve(SharedQuery, _fixture.BrandB, docType: null, k: 20);
            Assert.NotEmpty(result.Chunks);

            await using var admin = _fixture.CreateSuperuserContext();
            Assert.True(await admin.KnowledgeChunks.AsNoTracking().CountAsync(c => c.BrandId == _fixture.BrandA) > 0);

            foreach (var hit in result.Chunks)
            {
                var owner = await admin.KnowledgeChunks.AsNoTracking()
                    .Where(c => c.Id == hit.ChunkId).Select(c => c.BrandId).FirstAsync();
                Assert.Equal(_fixture.BrandB, owner);
            }
        }
    }
}
