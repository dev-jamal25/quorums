using Backend.Core.Domain;
using Backend.Core.Knowledge;
using Backend.Infrastructure.Configuration.Options;
using Xunit;

namespace Backend.IntegrationTests.Knowledge;

/// <summary>
/// Degrade-don't-crash (DL-022): a tei-rerank outage degrades to recall-order results plus a
/// structured ToolError — never an exception into the graph. (The query-transform degrade half
/// lands with Task 6.)
/// </summary>
[Collection("Knowledge")]
public sealed class RetrievalDegradeTests
{
    private readonly KnowledgeFixture _fixture;

    public RetrievalDegradeTests(KnowledgeFixture fixture) => _fixture = fixture;

    private sealed class ThrowingRerank : IRerankProvider
    {
        public Task<IReadOnlyList<RerankScore>> RerankAsync(string q, IReadOnlyList<string> d, CancellationToken ct = default)
            => throw new HttpRequestException("tei-rerank unreachable");
    }

    private sealed class ThrowingTransform : IQueryTransformer
    {
        public Task<IReadOnlyList<string>> ExpandAsync(string q, int variants, CancellationToken ct = default)
            => throw new HttpRequestException("query-transform LLM unreachable");
    }

    [Fact]
    public async Task Rerank_unreachable_degrades_to_recall_order_with_toolerror()
    {
        var opts = new RetrievalOptions { DenseEnabled = true, RerankEnabled = true };
        var (db, scope, retrieval) = _fixture.CreateRetrieval(_fixture.BrandA, opts, rerank: new ThrowingRerank());
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            var result = await retrieval.Retrieve(_fixture.BrandAProductQuery, _fixture.BrandA, docType: DocType.Product, k: 3);

            Assert.NotEmpty(result.Chunks);                  // recall-order fallback, no crash
            Assert.NotNull(result.Error);
            Assert.Equal("rerank.failed", result.Error!.Code);
        }
    }

    [Fact]
    public async Task Query_transform_unreachable_degrades_to_single_query_with_toolerror()
    {
        var opts = new RetrievalOptions { DenseEnabled = true, QueryTransformEnabled = true, RerankEnabled = false };
        var (db, scope, retrieval) = _fixture.CreateRetrieval(_fixture.BrandA, opts, transform: new ThrowingTransform());
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            var result = await retrieval.Retrieve(_fixture.BrandAProductQuery, _fixture.BrandA, docType: DocType.Product, k: 3);

            Assert.NotEmpty(result.Chunks);                  // ran on the single original query, no crash
            Assert.NotNull(result.Error);
            Assert.Equal("querytransform.failed", result.Error!.Code);
        }
    }
}
