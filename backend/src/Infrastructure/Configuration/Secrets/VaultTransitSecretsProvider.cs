using System.Text;
using Backend.Core.Secrets;
using Backend.Infrastructure.Configuration.Options;
using Microsoft.Extensions.Options;
using VaultSharp;
using VaultSharp.V1.AuthMethods.Token;
using VaultSharp.V1.SecretsEngines.Transit;

namespace Backend.Infrastructure.Configuration.Secrets;

/// <summary>
/// Per-brand Meta-token crypto via Vault Transit (DL-011). The key
/// <c>transit/keys/{TransitKeyName}</c> never leaves Vault; this provider calls encrypt-on-store and
/// decrypt-on-use. Selected only when Vault is enabled; the dev/CI path uses
/// <see cref="PassthroughSecretsProvider"/>. <c>Vault:Address</c> stores host:port only — the app
/// owns the <c>http://</c> prefix (DL-011 convention), as in <c>VaultConfigurationExtensions</c>.
/// Never logs plaintext or ciphertext.
/// </summary>
public sealed class VaultTransitSecretsProvider : ISecretsProvider
{
    private readonly VaultClient _client;
    private readonly VaultOptions _options;

    public VaultTransitSecretsProvider(IOptions<VaultOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;

        if (string.IsNullOrWhiteSpace(_options.Token))
        {
            throw new InvalidOperationException("Vault is enabled but no access token is configured (Vault:Token).");
        }

        _client = new VaultClient(new VaultClientSettings(
            $"http://{_options.Address}",
            new TokenAuthMethodInfo(_options.Token)));
    }

    public async Task<string> EncryptAsync(string plaintext, CancellationToken cancellationToken = default)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext));
        var response = await _client.V1.Secrets.Transit
            .EncryptAsync(
                _options.TransitKeyName,
                new EncryptRequestOptions { Base64EncodedPlainText = encoded },
                mountPoint: _options.TransitMount)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        return response.Data.CipherText;
    }

    public async Task<string> DecryptAsync(string ciphertext, CancellationToken cancellationToken = default)
    {
        var response = await _client.V1.Secrets.Transit
            .DecryptAsync(
                _options.TransitKeyName,
                new DecryptRequestOptions { CipherText = ciphertext },
                mountPoint: _options.TransitMount)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        return Encoding.UTF8.GetString(Convert.FromBase64String(response.Data.Base64EncodedPlainText));
    }
}
