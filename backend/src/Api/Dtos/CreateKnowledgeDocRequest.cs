using Backend.Core.Domain;
using Backend.Core.Knowledge;

namespace Backend.Api.Dtos;

/// <summary>Create a brand-knowledge doc. Persisted then ingested (chunk → embed → upsert).</summary>
public sealed record CreateKnowledgeDocRequest(
    string Title,
    DocType DocType,
    KnowledgeFacet? Facet,
    string Content,
    string? Source,
    KnowledgeChunkMetadata? Metadata);
