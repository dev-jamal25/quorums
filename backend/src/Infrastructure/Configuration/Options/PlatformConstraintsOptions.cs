using System.ComponentModel.DataAnnotations;
using Backend.Core.Generation.PlatformConstraints;

namespace Backend.Infrastructure.Configuration.Options;

/// <summary>
/// The global, deterministic <c>PlatformConstraints</c> config (DL-030) — the same for every
/// tenant, structural/format limits only, seeded in configuration (adding a surface is config, not
/// code). Maps to the pure-domain <see cref="PlatformConstraintSet"/> the validators consume; that
/// one definition is invoked by both generation and the publish-time re-check.
/// </summary>
public sealed class PlatformConstraintsOptions
{
    public const string SectionName = "PlatformConstraints";

    [Required]
    [MinLength(1)]
    public SurfaceConstraintsOptions[] Surfaces { get; init; } = [];

    public PlatformConstraintSet ToConstraintSet() =>
        new(Surfaces.Select(surface => surface.ToConstraints()));
}

/// <summary>One surface's bound constraints (DL-030).</summary>
public sealed class SurfaceConstraintsOptions
{
    [Required(AllowEmptyStrings = false)]
    public string Surface { get; init; } = default!;

    [Range(1, 10_000)]
    public int MaxHashtags { get; init; }

    [Range(1, 1_000_000)]
    public int MaxCaptionLength { get; init; }

    [Required]
    [MinLength(1)]
    public string[] AllowedAspectRatios { get; init; } = [];

    public SurfaceConstraints ToConstraints() =>
        new(Surface, MaxHashtags, MaxCaptionLength, [.. AllowedAspectRatios]);
}
