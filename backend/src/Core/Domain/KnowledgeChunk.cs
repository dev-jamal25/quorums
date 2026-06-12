namespace Backend.Core.Domain;

/// <summary>
/// A retrieval chunk derived from a <see cref="KnowledgeDoc"/>. Brand-scoped so RAG
/// rows fall under the same RLS surface as everything else (DL-010, DL-016).
/// </summary>
/// <remarks>
/// v1 models the chunk's identity and text only. The pgvector <c>embedding</c>
/// column (<c>vector(768)</c>) arrives with the RAG slice (build-order Day 5); the
/// table is RLS-scoped now, so the embedding inherits isolation when it lands.
/// </remarks>
public sealed class KnowledgeChunk : IBrandScoped
{
    public Guid Id { get; set; }

    public Guid BrandId { get; set; }

    public Guid KnowledgeDocId { get; set; }

    public int ChunkIndex { get; set; }

    public string Content { get; set; } = default!;

    public DateTimeOffset CreatedAt { get; set; }
}
