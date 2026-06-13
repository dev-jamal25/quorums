using Backend.Core.Domain;
using Backend.Core.Multitenancy;
using Backend.Core.Onboarding;
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
    private readonly AppDbContext _dbContext;
    private readonly IBrandContext _brandContext;
    private readonly IBrandScope _brandScope;

    public BrandOnboardingService(
        AppDbContext dbContext,
        IBrandContext brandContext,
        IBrandScope brandScope)
    {
        _dbContext = dbContext;
        _brandContext = brandContext;
        _brandScope = brandScope;
    }

    public async Task<Guid> OnboardAsync(
        BrandOnboardingCommand command,
        CancellationToken cancellationToken = default)
    {
        // The brand id is app-assigned here, never supplied by the caller.
        var brandId = Guid.NewGuid();

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
            AudienceSegments = [.. command.AudienceSegments],
            AudiencePainPoints = [.. command.AudiencePainPoints],
            ProductContext = command.ProductContext,
            CreatedAt = now,
        });

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await handle.CompleteAsync(cancellationToken).ConfigureAwait(false);

        return brandId;
    }
}
