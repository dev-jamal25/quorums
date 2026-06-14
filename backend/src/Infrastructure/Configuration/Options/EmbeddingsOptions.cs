using System.ComponentModel.DataAnnotations;

namespace Backend.Infrastructure.Configuration.Options;

/// <summary>
/// Self-hosted embedding server settings (DL-016). All non-secret config. The
/// dimension must equal the pgvector column dimension set in the EF migration.
/// </summary>
public sealed class EmbeddingsOptions
{
    public const string SectionName = "Embeddings";

    /// <summary>The nomic prefix applied to corpus chunks at ingest (DL-016).</summary>
    public const string DocumentPrefix = "search_document:";

    /// <summary>The nomic prefix applied to queries at retrieval (DL-016).</summary>
    public const string QueryPrefix = "search_query:";

    [Required(AllowEmptyStrings = false)]
    public string BaseUrl { get; init; } = default!;

    [Required(AllowEmptyStrings = false)]
    public string Model { get; init; } = default!;

    [Range(1, 4096)]
    public int Dimension { get; init; } = 768;

    /// <summary><c>nomic</c> (real tei-embed) or <c>mock</c> (deterministic, offline). CI uses mock.</summary>
    public string Mode { get; init; } = "nomic";
}
