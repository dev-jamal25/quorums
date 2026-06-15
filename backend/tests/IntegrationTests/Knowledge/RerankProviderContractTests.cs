using Backend.Infrastructure.Knowledge;
using Backend.IntegrationTests.Support;
using Xunit;

namespace Backend.IntegrationTests.Knowledge;

/// <summary>
/// CrossEncoderRerankProvider request shape over a recording handler (no network — CI never calls
/// TEI), plus an opt-in live test mirroring slice-2's LiveEmbeddings pattern.
/// </summary>
public sealed class RerankProviderContractTests
{
    private static CrossEncoderRerankProvider Provider(RecordingHttpMessageHandler h) =>
        new(new HttpClient(h) { BaseAddress = new Uri("http://tei-rerank") });

    [Fact]
    public async Task Rerank_posts_query_and_texts_to_rerank_endpoint()
    {
        var h = new RecordingHttpMessageHandler("[{\"index\":1,\"score\":0.9},{\"index\":0,\"score\":0.1}]");

        var scores = await Provider(h).RerankAsync("floral roast", ["espresso", "yirgacheffe floral"]);

        Assert.Contains("\"query\":\"floral roast\"", h.RequestBodies[0], StringComparison.Ordinal);
        Assert.Contains("\"texts\":[", h.RequestBodies[0], StringComparison.Ordinal);
        Assert.Contains("\"raw_scores\":false", h.RequestBodies[0], StringComparison.Ordinal);
        Assert.Equal(1, scores.OrderByDescending(s => s.Relevance).First().Index);
    }

    [Fact(Skip = "Opt-in live test: requires a running tei-rerank. Remove Skip to run locally.")]
    [Trait("Category", "LiveRerank")]
    public async Task Real_tei_rerank_ranks_matching_doc_first()
    {
        var endpoint = Environment.GetEnvironmentVariable("Reranker__Endpoint") ?? "localhost:8091";
        using var http = new HttpClient { BaseAddress = new Uri($"http://{endpoint}") };

        var scores = await new CrossEncoderRerankProvider(http).RerankAsync(
            "floral light roast",
            ["chocolate caramel medium-dark espresso", "floral jasmine bergamot light roast"]);

        Assert.Equal(1, scores.OrderByDescending(s => s.Relevance).First().Index);
    }
}
