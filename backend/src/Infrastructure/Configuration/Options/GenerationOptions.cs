using System.ComponentModel.DataAnnotations;

namespace Backend.Infrastructure.Configuration.Options;

/// <summary>
/// Generation-pipeline model selection (DL-027/029). The two text tiers are config-bound and
/// never literals in agent code: Strategist / Supervisor selection / Creative Director run on
/// <see cref="SonnetModel"/>, Copywriting on <see cref="HaikuModel"/>. Seeded with the current
/// live model ids at build/config time.
/// </summary>
public sealed class GenerationOptions
{
    public const string SectionName = "Generation";

    [Required(AllowEmptyStrings = false)]
    public string SonnetModel { get; init; } = default!;

    [Required(AllowEmptyStrings = false)]
    public string HaikuModel { get; init; } = default!;
}
