namespace Backend.Core.Knowledge;

/// <summary>
/// Embeds text with nomic-embed-text-v1.5's mandatory prefix split (DL-016): corpus
/// chunks carry <c>search_document:</c>, queries carry <c>search_query:</c>. Two methods,
/// not one with a flag — the prefix is the contract, so a mismatch is a compile-time
/// obvious mistake rather than a silent argument error.
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>Embeds a corpus chunk. Applies the <c>search_document:</c> prefix.</summary>
    Task<float[]> EmbedDocumentAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Embeds a query. Applies the <c>search_query:</c> prefix.</summary>
    Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken = default);
}
