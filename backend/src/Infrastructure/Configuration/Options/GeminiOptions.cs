using System.ComponentModel.DataAnnotations;

namespace Backend.Infrastructure.Configuration.Options;

/// <summary>
/// Gemini media-tool settings. The API key is a secret (Vault KV / environment);
/// the base URL, model id, API version, and resilience knobs are non-secret config.
/// The model/version/timeout/retries carry sensible defaults so a <c>Mode=mock</c> host
/// boots without them (only <see cref="ApiKey"/>/<see cref="BaseUrl"/> are required).
/// </summary>
public sealed class GeminiOptions
{
    public const string SectionName = "Gemini";

    [Required(AllowEmptyStrings = false)]
    public string ApiKey { get; init; } = default!;

    [Required(AllowEmptyStrings = false)]
    public string BaseUrl { get; init; } = default!;

    /// <summary>The image-generation model id (config-bound, never a code literal — DL-029/P3).</summary>
    public string Model { get; init; } = "gemini-2.5-flash-image";

    /// <summary>The Developer-API version path segment (<c>generateContent</c> lives under v1beta).</summary>
    public string ApiVersion { get; init; } = "v1beta";

    /// <summary>Per-attempt request timeout (seconds) — owned by the Polly timeout policy.</summary>
    [Range(1, 600)]
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>Bounded transient/429 retries before the tool fails the item (DL-023).</summary>
    [Range(0, 5)]
    public int MaxRetries { get; init; } = 2;
}
