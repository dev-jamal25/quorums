using System.ComponentModel.DataAnnotations;

namespace Backend.Infrastructure.Configuration.Options;

/// <summary>
/// Vault settings (DL-011). Non-secret topology (address, mounts, key name) has
/// dev defaults in appsettings.json; the access token is a secret supplied via
/// environment and is never committed or logged. When <see cref="Enabled"/> is
/// false the app falls back to appsettings.json + environment variables for KV
/// values (the dev path).
/// </summary>
public sealed class VaultOptions
{
    public const string SectionName = "Vault";

    /// <summary>When false, KV secrets come from appsettings/env, not Vault (dev fallback).</summary>
    public bool Enabled { get; init; }

    [Required(AllowEmptyStrings = false)]
    public string Address { get; init; } = default!;

    [Required(AllowEmptyStrings = false)]
    public string KvMount { get; init; } = default!;

    /// <summary>KV v2 path holding app-config secrets, read at startup when enabled.</summary>
    public string KvSecretPath { get; init; } = "quorums";

    [Required(AllowEmptyStrings = false)]
    public string TransitMount { get; init; } = default!;

    [Required(AllowEmptyStrings = false)]
    public string TransitKeyName { get; init; } = default!;

    /// <summary>Vault access token. Secret — env-supplied only; never committed or logged.</summary>
    public string? Token { get; init; }
}
