using System.Text;
using Backend.Core.Secrets;
using Backend.Infrastructure.Integrations.Meta;
using Backend.IntegrationTests.Durability;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Backend.IntegrationTests.Publishing;

/// <summary>
/// DL-055 / DL-011 secrets proof for the brand-connection seeding seam: the supplied token is persisted
/// as Transit ciphertext — the stored value is NOT the plaintext token — and is recovered only by a
/// decrypt at read time. Runs over the real RLS-bound Postgres (the durability fixture). The dev
/// passthrough provider is identity (ciphertext == plaintext), so this drives a transforming double to
/// prove the connector routes the token through <see cref="ISecretsProvider.EncryptAsync"/> BEFORE the
/// DB ever sees it (the real Vault Transit provider produces real ciphertext the same way).
/// </summary>
[Trait("Category", "Publish")]
[Collection("Durability")]
public sealed class BrandMetaConnectorTests
{
    private readonly DurabilityFixture _fixture;

    public BrandMetaConnectorTests(DurabilityFixture fixture) => _fixture = fixture;

    // A reversible transform so ciphertext != plaintext (unlike the identity passthrough), modelling
    // what Vault Transit does: encrypt-on-store, decrypt-on-use.
    private sealed class ReversibleSecrets : ISecretsProvider
    {
        public Task<string> EncryptAsync(string plaintext, CancellationToken cancellationToken = default) =>
            Task.FromResult("enc:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(plaintext)));

        public Task<string> DecryptAsync(string ciphertext, CancellationToken cancellationToken = default) =>
            Task.FromResult(Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext["enc:".Length..])));
    }

    [Fact]
    public async Task Connect_persists_token_as_ciphertext_not_plaintext_and_decrypts_only_on_read()
    {
        const string token = "live-page-token-do-not-store-in-clear";
        var secrets = new ReversibleSecrets();

        // A throwaway brand so connecting both channels does not leave a second channel on the shared
        // BrandA (which would double-publish later tests in the Durability collection).
        var brandId = Guid.NewGuid();
        await _fixture.SeedBrandAsync(brandId, "Connector Test Brand");

        var (db, scope, brandContext) = _fixture.CreateGateDeps(brandId);
        await using (db)
        {
            var connector = new BrandMetaConnector(db, brandContext, scope, secrets);
            await connector.ConnectAsync(
                brandId, token, facebookPageId: "fb-page-123", igBusinessAccountId: "ig-acct-456");
        }

        var (readDb, readScope) = _fixture.CreateReadContext(brandId);
        await using (readDb)
        {
            await using var handle = await readScope.BeginAsync();
            var row = await readDb.BrandMetaConnections.AsNoTracking().FirstAsync();

            // Stored encrypted — the plaintext token never lands in the column.
            Assert.NotEqual(token, row.TokenCiphertext);
            Assert.Equal(await secrets.EncryptAsync(token), row.TokenCiphertext);
            Assert.DoesNotContain(token, row.TokenCiphertext, StringComparison.Ordinal);

            // The non-secret channel target ids are stored in the clear.
            Assert.Equal("fb-page-123", row.FacebookPageId);
            Assert.Equal("ig-acct-456", row.IgBusinessAccountId);

            // Decrypt-on-use recovers the token (the only time it is in the clear).
            Assert.Equal(token, await secrets.DecryptAsync(row.TokenCiphertext));
        }
    }
}
