namespace Backend.Core.Knowledge;

/// <summary>
/// S0 multi-query expansion (DL-025). One query → N variants that widen recall only; the reranker
/// still scores the pool against the original query. Config-gated; off → the single original query.
/// </summary>
public interface IQueryTransformer
{
    Task<IReadOnlyList<string>> ExpandAsync(string query, int variants, CancellationToken cancellationToken = default);
}
