namespace Backend.Infrastructure.Configuration.Options;

/// <summary>
/// Media-pricing config beyond the per-image Gemini price (DL-058, extends DL-029). The per-second Veo
/// video price is config-bound here — never hardcoded in agent code nor recalled from memory; the
/// pre-Media budget gate multiplies it by the clip duration (plus the one image for an image-seed run).
/// Optional-with-default so an absent <c>Media</c> section never crashes startup or breaks image runs
/// (the image path never reads it); the shipped dev value lives in <c>appsettings.json</c>.
/// </summary>
public sealed class MediaOptions
{
    public const string SectionName = "Media";

    /// <summary>USD per second of generated video; multiplied by the clip duration at the pre-Media gate.</summary>
    public decimal VideoPricePerSec { get; init; }
}
