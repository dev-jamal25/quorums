using System.ComponentModel.DataAnnotations;

namespace Backend.Infrastructure.Configuration.Options;

/// <summary>
/// The regenerate bound (DL-036). <see cref="MaxPerRun"/> is the HARD per-run regenerate count — the
/// safety floor that makes the re-entrant gate non-loopable, independent of the DL-029 cost ceiling
/// (which is enforced separately in the Media node). Non-secret config.
/// </summary>
public sealed class RegenerationOptions
{
    public const string SectionName = "Regeneration";

    /// <summary>Maximum regenerate decisions allowed per run; further requests are rejected (409).</summary>
    [Range(0, 100)]
    public int MaxPerRun { get; init; }
}
