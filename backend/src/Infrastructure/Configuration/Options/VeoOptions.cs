using System.ComponentModel.DataAnnotations;

namespace Backend.Infrastructure.Configuration.Options;

/// <summary>
/// Veo 3.1 video-generation config (DL-058), all <b>optional-with-defaults</b> so an absent/disabled
/// Veo section never crashes startup or breaks image runs (the image path reads none of this). Like
/// <c>Gemini</c>, the api key is NOT here — the live Veo client reuses the same Gemini key from Vault.
/// <see cref="Mode"/> <c>mock</c> (default) uses the deterministic video tool; <c>live</c> wires the
/// real <c>LiveVeoClient</c>. <see cref="MaxDurationSec"/> caps the stamped clip length;
/// <see cref="PollTimeout"/> bounds the operation poll loop (a timeout degrades to caption-only).
/// </summary>
public sealed class VeoOptions
{
    public const string SectionName = "Veo";

    public string Mode { get; init; } = "mock";

    public string Model { get; init; } = "veo-3.1-fast-generate-preview";

    [Range(1, 60)]
    public int MaxDurationSec { get; init; } = 5;

    /// <summary>Hard ceiling on the operation poll loop (e.g. <c>"00:03:00"</c>); a breach degrades to caption-only.</summary>
    public TimeSpan PollTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Delay between polls of a pending Veo operation.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>True only when explicitly switched live; gates the real client + health check registration.</summary>
    public bool IsLive => string.Equals(Mode, "live", StringComparison.OrdinalIgnoreCase);
}
