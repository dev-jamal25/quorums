using Backend.Infrastructure.Configuration.Options;
using Backend.Infrastructure.Knowledge;
using Backend.IntegrationTests.Support;
using Microsoft.Extensions.Options;
using Xunit;

namespace Backend.IntegrationTests.Knowledge;

/// <summary>
/// Drives the real <see cref="NomicEmbeddingProvider"/> over an in-memory recording handler
/// (no network, no TEI — "CI never calls TEI") and asserts the mandatory nomic prefix split:
/// corpus carries <c>search_document:</c>, queries carry <c>search_query:</c>. A swap or a
/// drop is caught here, not silently in degraded recall (DL-016).
/// </summary>
public sealed class PrefixCorrectnessTests
{
    private static NomicEmbeddingProvider Provider(RecordingHttpMessageHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://tei-embed") },
            Options.Create(new EmbeddingsOptions { BaseUrl = "tei-embed:80", Model = "nomic", Dimension = 768 }));

    private static string Ok768() =>
        "[[" + string.Join(",", Enumerable.Repeat("0.1", 768)) + "]]";

    [Fact]
    public async Task Document_embed_uses_search_document_prefix()
    {
        var handler = new RecordingHttpMessageHandler(Ok768());

        await Provider(handler).EmbedDocumentAsync("roasted in small batches");

        Assert.Contains("search_document:roasted in small batches", handler.RequestBodies[0]);
        Assert.DoesNotContain("search_query:", handler.RequestBodies[0]); // a swap would fail here
    }

    [Fact]
    public async Task Query_embed_uses_search_query_prefix()
    {
        var handler = new RecordingHttpMessageHandler(Ok768());

        await Provider(handler).EmbedQueryAsync("what is your roast style");

        Assert.Contains("search_query:what is your roast style", handler.RequestBodies[0]);
        Assert.DoesNotContain("search_document:", handler.RequestBodies[0]);
    }
}
