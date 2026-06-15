using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Backend.Core.Knowledge;

namespace Backend.Infrastructure.Knowledge;

/// <summary>
/// bge-reranker-v2-m3 via HF TEI (tei-rerank, /rerank). Returns PURE cross-encoder relevance
/// (DL-025). Transport failures throw and are mapped to a <c>ToolError</c> by the caller (DL-022).
/// </summary>
public sealed class CrossEncoderRerankProvider : IRerankProvider
{
    private readonly HttpClient _http;

    public CrossEncoderRerankProvider(HttpClient http) => _http = http;

    public async Task<IReadOnlyList<RerankScore>> RerankAsync(
        string query, IReadOnlyList<string> documents, CancellationToken cancellationToken = default)
    {
        if (documents.Count == 0)
        {
            return [];
        }

        using var resp = await _http.PostAsJsonAsync(
            "/rerank", new RerankRequest(query, [.. documents], false), cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var ranked = await resp.Content
            .ReadFromJsonAsync<List<RerankResponseItem>>(cancellationToken).ConfigureAwait(false) ?? [];
        return ranked.Select(r => new RerankScore(r.Index, r.Score)).ToList();
    }

    private sealed record RerankRequest(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("texts")] string[] Texts,
        [property: JsonPropertyName("raw_scores")] bool RawScores);

    private sealed record RerankResponseItem(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("score")] double Score);
}
