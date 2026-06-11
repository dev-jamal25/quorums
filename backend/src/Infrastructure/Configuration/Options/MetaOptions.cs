using System.ComponentModel.DataAnnotations;

namespace Backend.Infrastructure.Configuration.Options;

/// <summary>
/// Meta integration mode. "mock" (default) runs the full loop with zero live Meta
/// access; "live" is the optional, human-gated path. Non-secret config.
/// </summary>
public sealed class MetaOptions
{
    public const string SectionName = "Meta";

    [Required(AllowEmptyStrings = false)]
    public string Mode { get; init; } = default!;
}
