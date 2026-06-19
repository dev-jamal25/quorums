namespace Backend.Core.Domain;

/// <summary>
/// Per-brand Meta credentials, stored as Transit-encrypted ciphertext in this
/// RLS-scoped table — never as plaintext. Vault Transit owns the key and decrypts
/// on-use inside <c>IMetaIntegration</c> at call time only (DL-011).
/// </summary>
public sealed class BrandMetaConnection : IBrandScoped
{
    public Guid Id { get; set; }

    public Guid BrandId { get; set; }

    /// <summary>Vault Transit ciphertext of the access token. Never the plaintext.</summary>
    public string TokenCiphertext { get; set; } = default!;

    public string TokenType { get; set; } = default!;

    /// <summary>
    /// Facebook Page id — the <c>TargetId</c> for the Facebook channel publish (DL-055). Non-secret
    /// (a public Page identifier), so it is a plain column, not Transit ciphertext. Null when the
    /// brand has no connected Facebook Page.
    /// </summary>
    public string? FacebookPageId { get; set; }

    /// <summary>
    /// Instagram Business Account id — the <c>TargetId</c> for the Instagram channel publish (DL-055).
    /// Non-secret (a public account identifier). Null when the brand has no connected Instagram account.
    /// </summary>
    public string? IgBusinessAccountId { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public string? Scopes { get; set; }

    public DateTimeOffset? RotatedAt { get; set; }
}
