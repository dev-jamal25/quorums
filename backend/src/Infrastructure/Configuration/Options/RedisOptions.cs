using System.ComponentModel.DataAnnotations;

namespace Backend.Infrastructure.Configuration.Options;

/// <summary>
/// Redis settings for <c>IDistributedCache</c> (caching only — not the queue
/// broker). The connection string is non-secret topology in dev.
/// </summary>
public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    [Required(AllowEmptyStrings = false)]
    public string Configuration { get; init; } = default!;
}
