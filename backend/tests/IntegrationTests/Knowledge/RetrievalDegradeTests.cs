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

    [Fact]
    public async Task Rerank_unreachable_degrades_to_recall_order_with_toolerror()
    {
        var opts = new RetrievalOptions { DenseEnabled = true, RerankEnabled = true };
        var (db, scope, retrieval) = _fixture.CreateRetrieval(_fixture.BrandA, opts, rerank: new ThrowingRerank());
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            var result = await retrieval.Retrieve(_fixture.BrandAProductQuery, _fixture.BrandA, docType: "Product", k: 3);

            Assert.NotEmpty(result.Chunks);                  // recall-order fallback, no crash
            Assert.NotNull(result.Error);
            Assert.Equal("rerank.failed", result.Error!.Code);
        }
    }
}
