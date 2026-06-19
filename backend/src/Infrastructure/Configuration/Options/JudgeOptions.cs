using System.ComponentModel.DataAnnotations;

namespace Backend.Infrastructure.Configuration.Options;

/// <summary>
/// LLM-judge tier settings (DL-057). The judges score each dimension on a 1–5 rubric; a structured verdict
/// is binarized at <see cref="PassThreshold"/> (config-bound, never a code literal) for the Cohen's-κ gate.
/// </summary>
public sealed class JudgeOptions
{
    public const string SectionName = "Judge";

    /// <summary>The 1–5 score a dimension must reach to count as a pass. Brand = ALL dimensions ≥ this;
    /// groundedness = the single groundedness score ≥ this.</summary>
    [Range(1, 5)]
    public int PassThreshold { get; init; } = 4;
}
