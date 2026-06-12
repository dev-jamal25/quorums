using Backend.Core.Onboarding;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Infrastructure.Onboarding;

/// <summary>
/// Registers the brand onboarding service. Scoped, because it depends on the
/// scoped <c>AppDbContext</c>, <c>IBrandContext</c>, and <c>IBrandScope</c> — one
/// per request/job. Call after <c>AddDataAccess()</c>.
/// </summary>
public static class OnboardingServiceCollectionExtensions
{
    public static IServiceCollection AddOnboarding(this IServiceCollection services)
    {
        services.AddScoped<IBrandOnboardingService, BrandOnboardingService>();
        return services;
    }
}
