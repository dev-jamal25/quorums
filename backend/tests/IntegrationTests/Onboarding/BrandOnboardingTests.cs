using Backend.Core.Onboarding;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Backend.IntegrationTests.Onboarding;

/// <summary>
/// End-to-end onboarding against the real <see cref="Backend.Infrastructure.Persistence.AppDbContext"/>,
/// the real <see cref="Backend.Core.Multitenancy.IBrandScope"/> binding, and a
/// non-owner role fully subject to RLS. Proves onboarding self-scopes: it writes
/// the new tenant THROUGH Row-Level Security, never around it.
/// </summary>
public sealed class BrandOnboardingTests : IClassFixture<OnboardingFixture>
{
    private readonly OnboardingFixture _fixture;

    public BrandOnboardingTests(OnboardingFixture fixture) => _fixture = fixture;

    private static BrandOnboardingCommand SampleCommand(string name) => new(
        Name: name,
        Positioning: "Specialty coffee for people who hate fuss.",
        ToneDescriptors: ["warm", "direct"],
        VoiceDo: ["speak plainly"],
        VoiceDont: ["no hype words"],
        ColorHexes: ["#1A2B3C", "#FFFFFF"],
        ImageryStyle: "Bright, minimal, natural light.",
        ContentPillars: ["Origin", "Craft", "Ritual"],
        AudienceSegments: ["urban professionals"],
        AudiencePainPoints: ["bad office coffee"],
        ProductContext: "Single-origin beans sold by subscription.");

    [Fact]
    public async Task Onboarding_creates_brand_and_profile_and_returns_the_new_id()
    {
        var (db, service) = _fixture.CreateOnboardingService();
        Guid brandId;
        await using (db)
        {
            brandId = await service.OnboardAsync(SampleCommand("Lumen Coffee"));
        }

        Assert.NotEqual(Guid.Empty, brandId);

        // Brand row (not brand-scoped) is visible to the app role; verify the name.
        await using (var appDb = _fixture.CreateAppContext())
        {
            var brand = await appDb.Brands.AsNoTracking().SingleOrDefaultAsync(b => b.Id == brandId);
            Assert.NotNull(brand);
            Assert.Equal("Lumen Coffee", brand!.Name);
        }

        // BrandProfile is brand-scoped: read it under the new brand's scope.
        var (scopedDb, scope) = _fixture.CreateBrandScopedContext(brandId);
        await using (scopedDb)
        {
            await using var handle = await scope.BeginAsync();

            var profile = await scopedDb.BrandProfiles.AsNoTracking().SingleOrDefaultAsync();
            Assert.NotNull(profile);
            Assert.Equal(brandId, profile!.BrandId);
            Assert.Equal("Specialty coffee for people who hate fuss.", profile.Positioning);
            Assert.Equal(["warm", "direct"], profile.ToneDescriptors);
            Assert.Equal(["#1A2B3C", "#FFFFFF"], profile.ColorHexes);
            Assert.Equal(["Origin", "Craft", "Ritual"], profile.ContentPillars);
            Assert.Equal("Single-origin beans sold by subscription.", profile.ProductContext);
        }
    }

    [Fact]
    [Trait("Category", "Isolation")]
    public async Task Onboarded_profile_is_visible_only_under_its_own_brand_context()
    {
        var (db, service) = _fixture.CreateOnboardingService();
        Guid brandId;
        await using (db)
        {
            brandId = await service.OnboardAsync(SampleCommand("Lumen Coffee"));
        }

        // Under the new brand's context: exactly one profile, and it is this brand's.
        var (ownDb, ownScope) = _fixture.CreateBrandScopedContext(brandId);
        await using (ownDb)
        {
            await using var handle = await ownScope.BeginAsync();
            var profiles = await ownDb.BrandProfiles.AsNoTracking().ToListAsync();
            Assert.Single(profiles);
            Assert.Equal(brandId, profiles[0].BrandId);
        }

        // Under no brand context: RLS predicate is NULL -> zero rows (fail closed).
        await using (var noScopeDb = _fixture.CreateAppContext())
        {
            var profiles = await noScopeDb.BrandProfiles.AsNoTracking().ToListAsync();
            Assert.Empty(profiles);
        }

        // Under a DIFFERENT brand's context: the onboarded profile is invisible,
        // even by primary-key lookup. Proves the write landed behind RLS.
        var (otherDb, otherScope) = _fixture.CreateBrandScopedContext(Guid.NewGuid());
        await using (otherDb)
        {
            await using var handle = await otherScope.BeginAsync();
            var profiles = await otherDb.BrandProfiles.AsNoTracking().ToListAsync();
            Assert.Empty(profiles);
        }
    }

    [Fact]
    [Trait("Category", "Isolation")]
    public async Task Onboarding_path_introduces_no_unscoped_write_a_cross_brand_insert_is_rejected()
    {
        // Onboarding writes only because brand_id == the bound scope. Prove the write
        // path is RLS-policed (WITH CHECK), so there is no bypass: scoped to one brand,
        // inserting a profile for a DIFFERENT brand must be rejected by the policy.
        var brandId = Guid.NewGuid();
        var foreignBrandId = Guid.NewGuid();

        var (db, scope) = _fixture.CreateBrandScopedContext(brandId);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            db.BrandProfiles.Add(new Backend.Core.Domain.BrandProfile
            {
                Id = Guid.NewGuid(),
                BrandId = foreignBrandId,
                Positioning = "x",
                ImageryStyle = "x",
                ProductContext = "x",
                CreatedAt = DateTimeOffset.UtcNow,
            });

            await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }
}
