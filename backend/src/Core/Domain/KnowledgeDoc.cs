namespace Backend.Core.Domain;

/// <summary>A manager-editable brand-knowledge document (the CMS corpus, DL-010).</summary>
public sealed class KnowledgeDoc : IBrandScoped
{
    public Guid Id { get; set; }

    public Guid BrandId { get; set; }

    public DocType DocType { get; set; }

    /// <summary>brand_playbook only: which slice this doc carries (voice/persona/…).</summary>
    public KnowledgeFacet? Facet { get; set; }

    public string Title { get; set; } = default!;

    /// <summary>Provenance (URL, author, file name). Optional.</summary>
    public string? Source { get; set; }

    public string Content { get; set; } = default!;

    /// <summary>Serialized <see cref="Knowledge.KnowledgeChunkMetadata"/> (jsonb). Promoted onto
    /// each chunk at ingest; structured, never embedded into chunk text (DL-026).</summary>
    public string? Metadata { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
