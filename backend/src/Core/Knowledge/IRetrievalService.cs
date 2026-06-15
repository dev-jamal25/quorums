using Backend.Core.Domain;
using Backend.Core.Orchestration;

namespace Backend.Core.Knowledge;

/// <summary>A ranked retrieval hit. Score is cosine similarity (1 = identical).</summary>
public sealed record RetrievedChunk(
    Guid ChunkId,
    Guid DocId,
    string Content,
    DocType DocType,
    KnowledgeFacet? Facet,
    double Score);

/// <summary>
/// The ranked chunks plus a <see cref="Grounded"/> flag (false on empty recall — degrade,
/// don't crash) and an optional <see cref="Error"/> (a provider/transport failure returns a
/// structured ToolError, never an exception into the graph — DL-022).
/// </summary>
public sealed record RetrievalResult(
    IReadOnlyList<RetrievedChunk> Chunks,
    bool Grounded,
    ToolError? Error = null);

/// <summary>
/// The only public retrieval surface (DL-025). The pipeline stages are internal to the
/// implementation. Brand isolation is the RLS policy via the bound BrandScope — never a
/// manual <c>WHERE brand_id</c>. <c>docType</c> is an explicit content filter — a typed
/// <see cref="DocType"/> (DL-033), not a string, so a caller can never pass an
/// unparseable/mis-cased value; <c>null</c> means "all doc types the caller may read".
/// </summary>
public interface IRetrievalService
{
    Task<RetrievalResult> Retrieve(string query, Guid brandId, DocType? docType, int k);
}
