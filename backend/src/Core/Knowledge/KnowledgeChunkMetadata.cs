namespace Backend.Core.Knowledge;

/// <summary>Structured fields promoted from a KnowledgeDoc onto its chunks at ingest.
/// Serialized to the chunk's jsonb column; read structurally by slice 3's filter/blend.
/// NEVER concatenated into chunk text before embedding (DL-026).</summary>
public sealed record KnowledgeChunkMetadata
{
    public double? EngagementRate { get; init; }
    public double? Ctr { get; init; }
    public string? AudienceSegment { get; init; }
    public string? Objective { get; init; }
    public DateTimeOffset? Date { get; init; }
    public string? ProductId { get; init; }
    public decimal? Price { get; init; }
    public string? Category { get; init; }
    public string? Source { get; init; }
    public bool? IsCompetitor { get; init; }
    public string? Platform { get; init; }
    public string? Surface { get; init; }
}
