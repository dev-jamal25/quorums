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

    public DateTimeOffset? ExpiresAt { get; set; }

    public string? Scopes { get; set; }

    public DateTimeOffset? RotatedAt { get; set; }
}
