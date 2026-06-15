namespace Backend.Core.Knowledge;

/// <summary>A pure cross-encoder relevance score for the doc at <see cref="Index"/> in the input list.</summary>
public sealed record RerankScore(int Index, double Relevance);

/// <summary>
/// Scores (query, doc) pairs with the bge-reranker cross-encoder (DL-025). Returns PURE
/// relevance — the metadata blend is <c>PgVectorRetrieval</c>'s job, never the provider's.
/// </summary>
public interface IRerankProvider
{
    Task<IReadOnlyList<RerankScore>> RerankAsync(
        string query, IReadOnlyList<string> documents, CancellationToken cancellationToken = default);
}
