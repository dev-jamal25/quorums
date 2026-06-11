using System.ComponentModel.DataAnnotations;

namespace Backend.Infrastructure.Configuration.Options;

/// <summary>
/// Postgres connection. Bound from the "ConnectionStrings" section. The
/// connection string carries credentials and is never committed — it is supplied
/// via environment / Actions secrets (or Vault KV in prod).
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "ConnectionStrings";

    [Required(AllowEmptyStrings = false)]
    public string Postgres { get; init; } = default!;
}
