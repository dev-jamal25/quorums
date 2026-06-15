using Backend.Infrastructure.Configuration.Options;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Backend.IntegrationTests.Knowledge;

/// <summary>
/// The critical new attack surface (DL-010): cross-brand leakage proof on the FTS (sparse) arm.
/// Dense isolation was proven in slice 2; the raw-SQL FTS arm is new. Bound to one brand, the
/// sparse-only pipeline must return zero of the other brand's chunks even though both brands hold
/// identical FTS-matching content (vacuity-guarded via the unscoped superuser).
/// </summary>
[Trait("Category", "Isolation")]
[Collection("Knowledge")]
public sealed class SparseArmIsolationTests
{
    private readonly KnowledgeFixture _fixture;

    public SparseArmIsolationTests(KnowledgeFixture fixture) => _fixture = fixture;

    // Sparse-only: dense OFF, sparse ON — isolates the FTS arm as the sole recall path.
    private static RetrievalOptions SparseOnly() => new()
    {
        DenseEnabled = false,
        SparseEnabled = true,
        RerankEnabled = false,
        QueryTransformEnabled = false,
    };

    [Fact]
    public async Task Sparse_arm_under_brand_A_returns_zero_brand_B_chunks()
    {
        var (db, scope, retrieval) = _fixture.CreateRetrieval(_fixture.BrandA, SparseOnly());
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            // "roast" is a brand-distinctive term present in BOTH brands' identical corpus.
            var result = await retrieval.Retrieve("roast style and brand voice", _fixture.BrandA, docType: null, k: 20);
            Assert.NotEmpty(result.Chunks);     // FTS actually matched something for A

            await using var admin = _fixture.CreateSuperuserContext();   // bypasses RLS — sees all brands
            // Vacuity guard: Brand B really has FTS-matching content, so only RLS keeps it out of A's result.
            Assert.True(await admin.KnowledgeChunks.AsNoTracking().CountAsync(c => c.BrandId == _fixture.BrandB) > 0);

            foreach (var hit in result.Chunks)
            {
                var owner = await admin.KnowledgeChunks.AsNoTracking()
                    .Where(c => c.Id == hit.ChunkId).Select(c => c.BrandId).FirstAsync();
                Assert.Equal(_fixture.BrandA, owner);   // zero B leakage on the sparse arm
            }
        }
    }

    [Fact]
    public async Task Sparse_arm_under_brand_B_returns_zero_brand_A_chunks()
    {
        var (db, scope, retrieval) = _fixture.CreateRetrieval(_fixture.BrandB, SparseOnly());
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            var result = await retrieval.Retrieve("roast style and brand voice", _fixture.BrandB, docType: null, k: 20);
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
