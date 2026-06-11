using System.ComponentModel.DataAnnotations;

namespace Backend.Infrastructure.Configuration.Options;

/// <summary>
/// Hangfire settings. The job store is PostgreSQL in a separate schema that holds
/// no brand data. Non-secret config.
/// </summary>
public sealed class HangfireOptions
{
    public const string SectionName = "Hangfire";

    [Required(AllowEmptyStrings = false)]
    public string Schema { get; init; } = default!;
}
