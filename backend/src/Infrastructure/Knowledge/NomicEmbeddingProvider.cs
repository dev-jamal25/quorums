using System.Net.Http.Json;
using Backend.Core.Domain;
using Backend.Core.Knowledge;
using Backend.Infrastructure.Configuration.Options;
using Microsoft.Extensions.Options;

namespace Backend.Infrastructure.Knowledge;

/// <summary>
/// nomic-embed-text-v1.5 via HF TEI (tei-embed). Applies the search_document: /
/// search_query: prefix split (DL-016) and asserts the returned dimension equals the
/// configured dimension (which equals the pgvector column dim). The HTTP client's
/// BaseAddress + resilience policy are wired at registration (AddKnowledge).
/// </summary>
public sealed class NomicEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _http;
    private readonly int _dimension;

    public NomicEmbeddingProvider(HttpClient http, IOptions<EmbeddingsOptions> options)
    {
        _http = http;
        _dimension = options.Value.Dimension;
    }

    public Task<float[]> EmbedDocumentAsync(string text, CancellationToken cancellationToken = default) =>
        EmbedAsync(EmbeddingsOptions.DocumentPrefix + text, cancellationToken);

    public Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken = default) =>
        EmbedAsync(EmbeddingsOptions.QueryPrefix + text, cancellationToken);

    private async Task<float[]> EmbedAsync(string prefixed, CancellationToken cancellationToken)
    {
        // TEI /embed contract: { "inputs": "<prefixed text>" } -> [[ ...768 floats... ]]
        using var response = await _http
            .PostAsJsonAsync("/embed", new { inputs = prefixed }, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var vectors = await response.Content
            .ReadFromJsonAsync<float[][]>(cancellationToken)
            .ConfigureAwait(false);

        var vector = vectors is { Length: > 0 } ? vectors[0] : [];
        if (vector.Length != _dimension)
        {
            throw new InvalidOperationException(
                $"Embedding dim {vector.Length} != configured {_dimension}; " +
                $"pgvector column is vector({KnowledgeChunk.EmbeddingDimension}).");
        }

        return vector;
    }
}
