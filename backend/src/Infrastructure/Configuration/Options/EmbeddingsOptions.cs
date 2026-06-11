using System.ComponentModel.DataAnnotations;

namespace Backend.Infrastructure.Configuration.Options;

/// <summary>
/// Self-hosted embedding server settings (DL-016). All non-secret config. The
/// dimension must equal the pgvector column dimension set in the EF migration.
/// </summary>
public sealed class EmbeddingsOptions
{
    public const string SectionName = "Embeddings";

    [Required(AllowEmptyStrings = false)]
    public string BaseUrl { get; init; } = default!;

    [Required(AllowEmptyStrings = false)]
    public string Model { get; init; } = default!;

    [Range(1, 4096)]
    public int Dimension { get; init; } = 768;
}
