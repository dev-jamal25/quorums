namespace Backend.Core.Onboarding;

/// <summary>
/// Creates a brand and its profile in a single self-scoped unit of work. Onboarding
/// is the one path that "becomes" the tenant it creates: it generates the new brand
/// id (app-assigned key), binds <c>IBrandContext</c> to that id, and writes inside
/// the brand-scoped work transaction so the inserts pass FORCE RLS
/// (<c>brand_id == current_setting('app.current_brand')</c>). There is no
/// unscoped/admin/RLS-bypass path (DL-002, DL-007).
/// </summary>
public interface IBrandOnboardingService
{
    /// <summary>
    /// Creates the brand + profile and returns the generated brand id. The brand id
    /// is generated here, never supplied by the caller.
    /// </summary>
    Task<Guid> OnboardAsync(BrandOnboardingCommand command, CancellationToken cancellationToken = default);
}
