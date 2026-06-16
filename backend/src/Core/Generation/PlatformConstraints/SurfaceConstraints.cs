namespace Backend.Core.Generation.PlatformConstraints;

/// <summary>
/// The frozen, structural platform constraints for one publishing surface (DL-030) — global and
/// deterministic, the same for every tenant, covering format limits only (distinct from the
/// per-brand soft <c>platform_guidance</c> RAG corpus and from content-policy compliance). The
/// first entry in <see cref="AllowedAspectRatios"/> is the canonical value stamped onto the brief
/// (DL-034 R8). These values are config-bound by the caller, never literals in agent code.
/// </summary>
public sealed record SurfaceConstraints(
    string Surface,
    int MaxHashtags,
    int MaxCaptionLength,
    IReadOnlyList<string> AllowedAspectRatios)
{
    /// <summary>The canonical aspect ratio stamped onto the brief for this surface (R8).</summary>
    public string CanonicalAspectRatio => AllowedAspectRatios[0];
}
