using Backend.Core.Domain;
using Backend.Core.Knowledge;

namespace Backend.Api.Dtos;

/// <summary>Replace a knowledge doc's fields; re-ingest replaces its chunks (no duplicates).</summary>
public sealed record UpdateKnowledgeDocRequest(
    string Title,
    DocType DocType,
    KnowledgeFacet? Facet,
    string Content,
    string? Source,
    KnowledgeChunkMetadata? Metadata);
