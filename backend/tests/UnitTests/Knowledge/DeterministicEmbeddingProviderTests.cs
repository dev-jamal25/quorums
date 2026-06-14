using Backend.Core.Domain;
using Backend.Infrastructure.Knowledge;
using Xunit;

namespace Backend.UnitTests.Knowledge;

public sealed class DeterministicEmbeddingProviderTests
{
    [Fact]
    public async Task Shared_vocabulary_is_nearer_than_disjoint_and_dim_is_768()
    {
        var provider = new DeterministicEmbeddingProvider();

        var doc = await provider.EmbedDocumentAsync("single origin espresso roast notes");
        var near = await provider.EmbedQueryAsync("espresso roast");
        var far = await provider.EmbedQueryAsync("matcha green tea ceremony");

        Assert.Equal(KnowledgeChunk.EmbeddingDimension, doc.Length);
        // Shared vocabulary ⇒ higher cosine — makes the dense-relevance test meaningful offline.
        Assert.True(Cosine(doc, near) > Cosine(doc, far));
    }

    [Fact]
    public async Task Same_content_embeds_identically_regardless_of_prefix_method()
    {
        var provider = new DeterministicEmbeddingProvider();

        var asDocument = await provider.EmbedDocumentAsync("espresso roast");
        var asQuery = await provider.EmbedQueryAsync("espresso roast");

        // The prefix is recorded, not folded into the vector — so a doc and a query of the
        // same text are nearest neighbours (the basis of the relevance test).
        Assert.Equal(asDocument, asQuery);
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }

        return dot; // both vectors are L2-normalized
    }
}
