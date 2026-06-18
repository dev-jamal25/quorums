using Backend.Core.Secrets;

namespace Backend.Infrastructure.Configuration.Secrets;

/// <summary>
/// Dev/CI secrets provider, selected when Vault is disabled (DL-011): ciphertext == plaintext, so the
/// encrypt→store→decrypt round-trip flows through the same <see cref="ISecretsProvider"/> seam without
/// a live Vault. Mirrors the KV loader's dev fallback in <c>VaultConfigurationExtensions</c>.
/// </summary>
public sealed class PassthroughSecretsProvider : ISecretsProvider
{
    public Task<string> EncryptAsync(string plaintext, CancellationToken cancellationToken = default) =>
        Task.FromResult(plaintext);

    public Task<string> DecryptAsync(string ciphertext, CancellationToken cancellationToken = default) =>
        Task.FromResult(ciphertext);
}
