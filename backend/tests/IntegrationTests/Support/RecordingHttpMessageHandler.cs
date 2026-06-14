using System.Text;

namespace Backend.IntegrationTests.Support;

/// <summary>
/// An in-memory <see cref="HttpMessageHandler"/> that records outbound request bodies and
/// returns a canned response — lets a test drive the real typed HttpClient provider (e.g.
/// <c>NomicEmbeddingProvider</c>) and assert what it sent, with no network and no TEI.
/// </summary>
public sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly string _responseJson;

    public RecordingHttpMessageHandler(string responseJson) => _responseJson = responseJson;

    public List<string> RequestBodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        RequestBodies.Add(body);

        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(_responseJson, Encoding.UTF8, "application/json"),
        };
    }
}
