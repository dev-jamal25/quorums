using System.ComponentModel.DataAnnotations;

namespace Backend.Infrastructure.Configuration.Options;

/// <summary>
/// Meta integration mode + live Graph API settings. "mock" (default) runs the full loop with zero live
/// Meta access; "live" is the optional, human-gated path (DL-055). The Graph fields are non-secret and
/// carry defaults so mock mode never needs them set; the per-brand token is NOT here (it is Transit
/// ciphertext in <c>BrandMetaConnection</c>, decrypted on-use, DL-011).
/// </summary>
public sealed class MetaOptions
{
    public const string SectionName = "Meta";

    [Required(AllowEmptyStrings = false)]
    public string Mode { get; init; } = default!;

    /// <summary>Graph API base URL (no trailing path/version). Config-bound, never a literal at the call site.</summary>
    public string GraphBaseUrl { get; init; } = "https://graph.facebook.com";

    /// <summary>Graph API version segment, e.g. <c>v21.0</c>. Config-bound so the version is swappable.</summary>
    public string GraphApiVersion { get; init; } = "v21.0";

    /// <summary>Per-attempt HTTP timeout (seconds) for the live Graph client's Polly timeout policy.</summary>
    public int TimeoutSeconds { get; init; } = 60;

    /// <summary>Bounded in-call transient/429 retries for the live Graph client's Polly retry policy.</summary>
    public int MaxRetries { get; init; } = 3;
}
