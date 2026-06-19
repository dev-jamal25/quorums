using Backend.Core.Domain;
using Backend.Core.Multitenancy;
using Backend.Core.Secrets;
using Backend.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Backend.Infrastructure.Integrations.Meta;

/// <summary>
/// Brand-connection seeding seam for the live Meta path (DL-055): Transit-encrypts a supplied
/// long-lived/Page token and upserts the brand's <see cref="BrandMetaConnection"/> with the token
/// ciphertext plus the non-secret channel target ids (Facebook Page id, IG Business Account id) and
/// token metadata. Invoked by the <c>meta-connect</c> CLI command; the token arrives via a transient
/// env var and is encrypted BEFORE it touches the DB — never logged or echoed.
/// <para>RLS-scoped exactly like onboarding: it binds <see cref="IBrandContext"/> to the (existing)
/// brand id and writes inside the brand-scoped work transaction (<see cref="IBrandScope"/>), so the
/// upsert passes FORCE RLS (<c>brand_id == current_setting</c>) with no unscoped/bypass path. The
/// token is encrypted before the transaction opens, so a live Vault Transit call never holds a DB
/// transaction open (DL-011).</para>
/// </summary>
public sealed class BrandMetaConnector
{
    private readonly AppDbContext _db;
    private readonly IBrandContext _brandContext;
    private readonly IBrandScope _brandScope;
    private readonly ISecretsProvider _secrets;

    public BrandMetaConnector(
        AppDbContext db,
        IBrandContext brandContext,
        IBrandScope brandScope,
        ISecretsProvider secrets)
    {
        _db = db;
        _brandContext = brandContext;
        _brandScope = brandScope;
        _secrets = secrets;
    }

    /// <summary>
    /// Upserts the brand's Meta connection: encrypts <paramref name="token"/> on store and persists it
    /// with the channel target ids and metadata. At least one of <paramref name="facebookPageId"/> /
    /// <paramref name="igBusinessAccountId"/> should be set (it determines the brand's connected channels).
    /// </summary>
    public async Task ConnectAsync(
        Guid brandId,
        string token,
        string? facebookPageId,
        string? igBusinessAccountId,
        string tokenType = "page",
        DateTimeOffset? expiresAt = null,
        string? scopes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        // Encrypt BEFORE opening the work transaction (a live Vault Transit call must not hold a DB
        // transaction open). Passthrough in dev/CI.
        var ciphertext = await _secrets.EncryptAsync(token, cancellationToken).ConfigureAwait(false);

        _brandContext.Bind(brandId);
        await using var handle = await _brandScope.BeginAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var existing = await _db.BrandMetaConnections
            .FirstOrDefaultAsync(cancellationToken)   // RLS-scoped to the bound brand (one row per brand)
            .ConfigureAwait(false);

        if (existing is null)
        {
            _db.BrandMetaConnections.Add(new BrandMetaConnection
            {
                Id = Guid.NewGuid(),
                BrandId = brandId,
                TokenCiphertext = ciphertext,
                TokenType = tokenType,
                FacebookPageId = facebookPageId,
                IgBusinessAccountId = igBusinessAccountId,
                ExpiresAt = expiresAt,
                Scopes = scopes,
                RotatedAt = now,
            });
        }
        else
        {
            existing.TokenCiphertext = ciphertext;
            existing.TokenType = tokenType;
            existing.FacebookPageId = facebookPageId;
            existing.IgBusinessAccountId = igBusinessAccountId;
            existing.ExpiresAt = expiresAt;
            existing.Scopes = scopes;
            existing.RotatedAt = now;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await handle.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
