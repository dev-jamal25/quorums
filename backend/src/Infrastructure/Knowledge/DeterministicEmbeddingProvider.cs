using Backend.Core.Domain;
using Backend.Core.Knowledge;
using Backend.Infrastructure.Configuration.Options;

namespace Backend.Infrastructure.Knowledge;

/// <summary>
/// Offline, deterministic embedding for CI (DL-016). A hashed token-frequency vector:
/// shared vocabulary ⇒ cosine proximity, so the dense-relevance test is meaningful
/// without a model server. Applies + records the prefix exactly like the real provider,
/// but the prefix is NOT folded into the vector — so a document and a query of the same
/// text embed identically (they are nearest neighbours).
/// </summary>
public sealed class DeterministicEmbeddingProvider : IEmbeddingProvider
{
    private const int Dim = KnowledgeChunk.EmbeddingDimension;

    public string? LastDocumentPrefix { get; private set; }

    public string? LastQueryPrefix { get; private set; }

    public Task<float[]> EmbedDocumentAsync(string text, CancellationToken cancellationToken = default)
    {
        LastDocumentPrefix = EmbeddingsOptions.DocumentPrefix;
        return Task.FromResult(Embed(text));
    }

    public Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken = default)
    {
        LastQueryPrefix = EmbeddingsOptions.QueryPrefix;
        return Task.FromResult(Embed(text));
    }

    private static float[] Embed(string text)
    {
        var vector = new float[Dim];
        foreach (var token in text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = new string(token.Where(char.IsLetterOrDigit).ToArray());
            if (clean.Length == 0)
            {
                continue;
            }

            var slot = (int)(unchecked((uint)StableHash(clean)) % Dim);
            vector[slot] += 1f;
        }

        var norm = (float)Math.Sqrt(vector.Sum(x => (double)x * x));
        if (norm > 0)
        {
            for (var i = 0; i < Dim; i++)
            {
                vector[i] /= norm;
            }
        }

        return vector;
    }

    // Stable across runs/processes (string.GetHashCode is randomized per process).
    private static int StableHash(string value)
    {
        unchecked
        {
            var hash = 17;
            foreach (var c in value)
            {
                hash = (hash * 31) + c;
            }

            return hash;
        }
    }
}
