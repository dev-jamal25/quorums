using Xunit;

namespace Backend.IntegrationTests.Knowledge;

/// <summary>
/// Empty recall must degrade, not crash (DL-022): a brand with no matching corpus returns
/// an ungrounded, empty result with no error and no exception.
/// </summary>
[Trait("Category", "Isolation")]
[Collection("Knowledge")]
public sealed class EmptyRetrievalTests
{
    private readonly KnowledgeFixture _fixture;

    public EmptyRetrievalTests(KnowledgeFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task No_matching_corpus_returns_ungrounded_empty_no_throw()
    {
        var (db, scope, retrieval) = _fixture.CreateRetrieval(_fixture.BrandWithNoCorpus);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            var result = await retrieval.Retrieve("anything at all", _fixture.BrandWithNoCorpus, docType: null, k: 5);

            Assert.False(result.Grounded);
            Assert.Empty(result.Chunks);
            Assert.Null(result.Error);
        }
    }
}
