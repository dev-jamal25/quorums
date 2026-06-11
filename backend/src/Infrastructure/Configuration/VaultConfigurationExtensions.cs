using Backend.Infrastructure.Configuration.Options;
using Microsoft.Extensions.Configuration;
using VaultSharp;
using VaultSharp.V1.AuthMethods.Token;
using VaultSharp.V1.Commons;

namespace Backend.Infrastructure.Configuration;

/// <summary>
/// Loads app-config secrets from Vault KV (v2) into configuration at startup
/// (DL-011), so the validated Options bind from one uniform source. Runs fully
/// async (no sync-over-async). When Vault is disabled the loader is a no-op and
/// the app falls back to appsettings.json + environment variables (the dev path).
/// Secret values are never logged.
/// </summary>
public static class VaultConfigurationExtensions
{
    /// <summary>
    /// Reads the configured KV path and layers its fields on top of existing
    /// configuration. KV field keys use .NET configuration paths (e.g.
    /// "Anthropic:ApiKey", "ConnectionStrings:Postgres").
    /// </summary>
    public static async Task AddVaultKvSecretsAsync(
        this ConfigurationManager configuration,
        CancellationToken cancellationToken = default)
    {
        var vault = configuration.GetSection(VaultOptions.SectionName).Get<VaultOptions>();
        if (vault is null || !vault.Enabled)
        {
            // Dev fallback: configuration comes from appsettings.json + environment.
            return;
        }

        if (string.IsNullOrWhiteSpace(vault.Token))
        {
            throw new InvalidOperationException(
                "Vault is enabled but no access token is configured (Vault:Token).");
        }

        var client = new VaultClient(new VaultClientSettings(vault.Address, new TokenAuthMethodInfo(vault.Token)));

        Secret<SecretData> secret = await client.V1.Secrets.KeyValue.V2
            .ReadSecretAsync(path: vault.KvSecretPath, mountPoint: vault.KvMount)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        var loaded = secret.Data.Data
            .Where(field => field.Value is not null)
            .Select(field => new KeyValuePair<string, string?>(field.Key, field.Value.ToString()));

        configuration.AddInMemoryCollection(loaded);
    }
}
