using Pgvector;

namespace Backend.Core.Domain;

/// <summary>
/// A retrieval chunk derived from a <see cref="KnowledgeDoc"/>. Brand-scoped so RAG
/// rows fall under the same RLS surface as everything else (DL-010, DL-016).
/// </summary>
public sealed class KnowledgeChunk : IBrandScoped
{
    /// <summary>The embedding dimension. MUST equal <c>EmbeddingsOptions.Dimension</c> and the
    /// pgvector column dim (DL-016). Single source of truth for both.</summary>
    public const int EmbeddingDimension = 768;

    public Guid Id { get; set; }

    public Guid BrandId { get; set; }

    public Guid KnowledgeDocId { get; set; }

    public int ChunkIndex { get; set; }

    public DocType DocType { get; set; }

    /// <summary>Copied from the doc; brand_playbook only otherwise null.</summary>
    public KnowledgeFacet? Facet { get; set; }

    /// <summary>The chunk text — clean, NO metadata concatenated in (DL-026).</summary>
    public string Content { get; set; } = default!;

    /// <summary>pgvector <c>vector(768)</c>; null until embedded. Cosine distance, normalized.</summary>
    public Vector? Embedding { get; set; }

    /// <summary>Serialized <see cref="Knowledge.KnowledgeChunkMetadata"/> (jsonb). The S1 filter and
    /// S2 blend (slice 3) read it structurally; never embedded into <see cref="Content"/>.</summary>
    public string? Metadata { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
