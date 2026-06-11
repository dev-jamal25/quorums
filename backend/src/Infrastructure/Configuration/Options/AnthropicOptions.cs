using System.ComponentModel.DataAnnotations;

namespace Backend.Infrastructure.Configuration.Options;

/// <summary>
/// Claude (orchestration brain) credentials. The API key is a secret sourced from
/// Vault KV / environment — never committed or logged.
/// </summary>
public sealed class AnthropicOptions
{
    public const string SectionName = "Anthropic";

    [Required(AllowEmptyStrings = false)]
    public string ApiKey { get; init; } = default!;
}
