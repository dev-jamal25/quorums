namespace Backend.Core.Secrets;

/// <summary>
/// The secrets crypto seam (DL-011). Per-brand Meta tokens are stored as Vault Transit ciphertext in
/// the RLS-scoped <c>BrandMetaConnection</c> and decrypted on-use at publish time only — the key
/// never leaves Vault. Two implementations: the real Vault Transit provider (when Vault is enabled)
/// and a dev passthrough (CI/dev, Vault-free) so the encrypt→store→decrypt flow runs through one seam.
/// Implementations never log plaintext or ciphertext.
/// </summary>
public interface ISecretsProvider
{
    /// <summary>Encrypt a token for storage (encrypt-on-store).</summary>
    Task<string> EncryptAsync(string plaintext, CancellationToken cancellationToken = default);

    /// <summary>Decrypt a stored token at the moment of use (decrypt-on-use).</summary>
    Task<string> DecryptAsync(string ciphertext, CancellationToken cancellationToken = default);
}
