using Backend.Core.Domain;
using Backend.Core.Multitenancy;
using Backend.Core.Onboarding;
using Backend.Core.Secrets;
using Backend.Infrastructure.Persistence;

namespace Backend.Infrastructure.Onboarding;

/// <summary>
/// Self-scoping brand onboarding (DL-002, DL-007). Generates the new brand id,
/// binds <see cref="IBrandContext"/> to it, and writes Brand + BrandProfile inside
/// the brand-scoped work transaction opened by <see cref="IBrandScope"/> — whose
/// first statement is <c>set_config('app.current_brand', newId, true)</c>. The
/// inserts therefore pass FORCE RLS (<c>brand_id == current_setting</c>). There is
/// no unscoped/admin/bypass path; onboarding becomes the tenant it creates.
/// </summary>
internal sealed class BrandOnboardingService : IBrandOnboardingService
{
    // Dummy demo token: the mock never calls Meta; the encrypt→store→decrypt round-trip is what is real.
    private const string DemoMetaToken = "demo-meta-token";

    private readonly AppDbContext _dbContext;
    private readonly IBrandContext _brandContext;
    private readonly IBrandScope _brandScope;
    private readonly ISecretsProvider _secrets;

    public BrandOnboardingService(
        AppDbContext dbContext,
        IBrandContext brandContext,
        IBrandScope brandScope,
        ISecretsProvider secrets)
    {
        _dbContext = dbContext;
        _brandContext = brandContext;
        _brandScope = brandScope;
        _secrets = secrets;
    }

    public async Task<Guid> OnboardAsync(
        BrandOnboardingCommand command,
        CancellationToken cancellationToken = default)
    {
        // The brand id is app-assigned here, never supplied by the caller.
        var brandId = Guid.NewGuid();

        // Encrypt the demo Meta token BEFORE opening the work transaction, so a (live) Vault Transit
        // call never holds a DB transaction open (DL-011). Passthrough in dev/CI.
        var tokenCiphertext = await _secrets.EncryptAsync(DemoMetaToken, cancellationToken).ConfigureAwait(false);

        // Self-scope: bind the context to the id we just minted (NOT from auth — the
        // brand does not exist yet), so the work transaction's first statement binds
        // app.current_brand to it. Every insert below is then admitted by FORCE RLS
        // because brand_id == current_setting.
        _brandContext.Bind(brandId);

        await using var handle = await _brandScope.BeginAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;

        // Brand is the scope itself (no RLS); the profile is brand-scoped and only
        // passes WITH CHECK because its brand_id equals the bound scope.
        _dbContext.Brands.Add(new Brand
        {
            Id = brandId,
            Name = command.Name,
            CreatedAt = now,
        });

        _dbContext.BrandProfiles.Add(new BrandProfile
        {
            Id = Guid.NewGuid(),
            BrandId = brandId,
            Positioning = command.Positioning,
            ToneDescriptors = [.. command.ToneDescriptors],
            VoiceDo = [.. command.VoiceDo],
            VoiceDont = [.. command.VoiceDont],
            ColorHexes = [.. command.ColorHexes],
            ImageryStyle = command.ImageryStyle,
            ContentPillars = [.. command.ContentPillars],
            AudienceSegments = [.. command.AudienceSegments],
            AudiencePainPoints = [.. command.AudiencePainPoints],
            ProductContext = command.ProductContext,
            CreatedAt = now,
        });

        // Per-brand Meta credentials as Transit ciphertext (DL-011), brand-scoped (FORCE RLS). Demo
        // value; decrypted on-use only at publish time inside the publish node.
        _dbContext.BrandMetaConnections.Add(new BrandMetaConnection
        {
            Id = Guid.NewGuid(),
            BrandId = brandId,
            TokenCiphertext = tokenCiphertext,
            TokenType = "bearer",
        });

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await handle.CompleteAsync(cancellationToken).ConfigureAwait(false);

        return brandId;
    }
}
