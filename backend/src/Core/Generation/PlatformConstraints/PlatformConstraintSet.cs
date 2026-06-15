namespace Backend.Core.Generation.PlatformConstraints;

/// <summary>
/// The resolved set of per-surface <see cref="SurfaceConstraints"/> (DL-030). Built from
/// config by the Infrastructure options binder and looked up by surface key (e.g.
/// <c>instagram_feed</c>). Adding a surface is config, not code.
/// </summary>
public sealed class PlatformConstraintSet
{
    private readonly Dictionary<string, SurfaceConstraints> _bySurface;

    public PlatformConstraintSet(IEnumerable<SurfaceConstraints> surfaces)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        _bySurface = surfaces.ToDictionary(
            surface => surface.Surface,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The constraints for a surface; throws if the surface is not configured.</summary>
    public SurfaceConstraints For(string surface)
    {
        if (_bySurface.TryGetValue(surface, out var constraints))
        {
            return constraints;
        }

        throw new KeyNotFoundException($"no PlatformConstraints configured for surface '{surface}'.");
    }

    /// <summary>Whether a surface is configured (for a deterministic, throw-free probe).</summary>
    public bool TryGet(string surface, out SurfaceConstraints constraints) =>
        _bySurface.TryGetValue(surface, out constraints!);
}
