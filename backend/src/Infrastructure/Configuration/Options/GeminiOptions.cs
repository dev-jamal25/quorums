using System.ComponentModel.DataAnnotations;

namespace Backend.Infrastructure.Configuration.Options;

/// <summary>
/// Gemini media-tool settings. The API key is a secret (Vault KV / environment);
/// the base URL is non-secret config.
/// </summary>
public sealed class GeminiOptions
{
    public const string SectionName = "Gemini";

    [Required(AllowEmptyStrings = false)]
    public string ApiKey { get; init; } = default!;

    [Required(AllowEmptyStrings = false)]
    public string BaseUrl { get; init; } = default!;
}
