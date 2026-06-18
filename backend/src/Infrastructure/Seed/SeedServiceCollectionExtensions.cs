using Microsoft.Extensions.DependencyInjection;

namespace Backend.Infrastructure.Seed;

/// <summary>
/// Registers the idempotent demo seeder. Invoked from the Api host's <c>seed</c> CLI mode
/// (<c>dotnet Backend.Api.dll seed</c>); it creates its own DI scopes, so it is a singleton.
/// </summary>
public static class SeedServiceCollectionExtensions
{
    public static IServiceCollection AddDemoSeed(this IServiceCollection services)
    {
        services.AddSingleton<DemoSeeder>();
        return services;
    }
}
