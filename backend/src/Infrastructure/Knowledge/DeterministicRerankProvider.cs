using Backend.Core.Knowledge;

namespace Backend.Infrastructure.Knowledge;

/// <summary>
/// Offline, deterministic cross-encoder stand-in for CI (DL-025). Relevance = normalized
/// query/doc token overlap, so a lexically closer doc scores higher and the union order is
/// meaningfully reordered — enough to prove the reranker engages without a model server.
/// </summary>
public sealed class DeterministicRerankProvider : IRerankProvider
{
    public Task<IReadOnlyList<RerankScore>> RerankAsync(
        string query, IReadOnlyList<string> documents, CancellationToken cancellationToken = default)
    {
        var q = Tokens(query);
        var scores = new List<RerankScore>(documents.Count);
        for (var i = 0; i < documents.Count; i++)
        {
            var d = Tokens(documents[i]);
            var overlap = q.Count == 0 ? 0.0 : q.Intersect(d).Count() / (double)q.Count;
            scores.Add(new RerankScore(i, overlap));
        }

        return Task.FromResult<IReadOnlyList<RerankScore>>(scores);
    }

    private static HashSet<string> Tokens(string text) =>
        text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => new string(t.Where(char.IsLetterOrDigit).ToArray()))
            .Where(t => t.Length > 0)
            .ToHashSet();
}
