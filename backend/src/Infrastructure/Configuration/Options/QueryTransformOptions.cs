using System.ComponentModel.DataAnnotations;

namespace Backend.Infrastructure.Configuration.Options;

/// <summary>
/// Multi-query expander settings (S0, DL-025). <see cref="Model"/> is config-bound, seeded at
/// build time from the current Haiku model (claude-haiku-4-5) — never hardcoded in code or
/// recalled from memory. <see cref="Mode"/>: <c>chat</c> (real Anthropic-backed
/// Microsoft.Extensions.AI <c>IChatClient</c>) or <c>mock</c> (deterministic, offline). CI uses mock.
/// </summary>
public sealed class QueryTransformOptions
{
    public const string SectionName = "QueryTransform";

    [Required(AllowEmptyStrings = false)]
    public string Model { get; init; } = "claude-haiku-4-5";

    /// <summary><c>chat</c> (real IChatClient) or <c>mock</c> (deterministic, offline). CI uses mock.</summary>
    public string Mode { get; init; } = "chat";
}
