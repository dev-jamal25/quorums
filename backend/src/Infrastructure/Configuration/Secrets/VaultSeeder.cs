using System.Net;
using Backend.Infrastructure.Configuration.Options;
using Microsoft.Extensions.Configuration;
using VaultSharp;
using VaultSharp.Core;
using VaultSharp.V1.AuthMethods.Token;
using VaultSharp.V1.SecretsEngines;
using VaultSharp.V1.SecretsEngines.Transit;

namespace Backend.Infrastructure.Configuration.Secrets;

/// <summary>
/// Idempotent seeder for the dev-mode Vault (DL-011). Repeatable after every Vault restart (dev mode
/// is in-memory and wipes KV + Transit on restart). It:
/// <list type="number">
///   <item>writes the app-config SECRETS to KV (values read from the local config/.env source — never
///   hardcoded, never logged);</item>
///   <item>ensures the Transit engine is mounted;</item>
///   <item>ensures the <c>transit/keys/{TransitKeyName}</c> encryption key exists (create-if-missing).</item>
/// </list>
/// Runs as the Api host's <c>vault-seed</c> CLI mode BEFORE the KV loader, so it works against an
/// unseeded Vault. Only credentials move to Vault; non-secret config (hosts, ports, flags) stays put.
/// </summary>
public static class VaultSeeder
{
    /// <summary>
    /// The genuine app-config credentials that live in Vault KV, by .NET config key. Non-secret config
    /// (BaseUrls, hosts, model ids, flags) is deliberately excluded — it stays in appsettings/env.
    /// </summary>
    private static readonly string[] _secretKeys =
    [
        "Anthropic:ApiKey",
        "Gemini:ApiKey",
        "ConnectionStrings:Postgres",
        "Minio:AccessKey",
        "Minio:SecretKey",
        "Langfuse:PublicKey",
        "Langfuse:SecretKey",
    ];

    public static async Task RunAsync(IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var vault = configuration.GetSection(VaultOptions.SectionName).Get<VaultOptions>()
            ?? throw new InvalidOperationException("Vault config (section 'Vault') is required to seed.");
        if (string.IsNullOrWhiteSpace(vault.Token))
        {
            throw new InvalidOperationException("Vault:Token is required to seed (the dev root token).");
        }

        // Vault:Address is host:port only — the app owns the http:// prefix (DL-011 convention).
        var client = new VaultClient(new VaultClientSettings(
            $"http://{vault.Address}",
            new TokenAuthMethodInfo(vault.Token)));

        // 1) KV: collect the present secret values from the local source and write them. The values are
        // held only in memory and passed straight to Vault — NEVER logged (DL-011).
        var data = new Dictionary<string, object>();
        var skipped = new List<string>();
        foreach (var key in _secretKeys)
        {
            var value = configuration[key];
            if (string.IsNullOrWhiteSpace(value))
            {
                skipped.Add(key);
                continue;
            }

            data[key] = value;
        }

        if (data.Count > 0)
        {
            await client.V1.Secrets.KeyValue.V2
                .WriteSecretAsync(vault.KvSecretPath, data, mountPoint: vault.KvMount)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        // 2) Transit engine — enable if not already mounted (idempotent).
        await EnsureTransitMountedAsync(client, vault.TransitMount, cancellationToken).ConfigureAwait(false);

        // 3) Transit key — create if missing. Vault no-ops a create on an existing key, so it is
        // idempotent and never rotates/destroys an existing key.
        await client.V1.Secrets.Transit
            .CreateEncryptionKeyAsync(vault.TransitKeyName, new CreateKeyRequestOptions(), mountPoint: vault.TransitMount)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);

        // Report COUNTS and key NAMES only — never a value.
        Console.WriteLine();
        Console.WriteLine(
            $"Vault seed complete  kv={vault.KvMount}/{vault.KvSecretPath}  secrets_written={data.Count}  "
            + $"transit_key={vault.TransitMount}/keys/{vault.TransitKeyName}");
        if (skipped.Count > 0)
        {
            Console.WriteLine($"  absent in source, skipped: {string.Join(", ", skipped)}");
        }

        Console.WriteLine();
    }

    private static async Task EnsureTransitMountedAsync(
        VaultClient client, string mount, CancellationToken cancellationToken)
    {
        try
        {
            await client.V1.System
                .MountSecretBackendAsync(new SecretsEngine { Type = SecretsEngineType.Transit, Path = mount })
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (VaultApiException ex) when (ex.HttpStatusCode == HttpStatusCode.BadRequest)
        {
            // Already enabled ("path is already in use") — idempotent, nothing to do.
        }
    }
}
