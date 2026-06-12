using Backend.Core.Multitenancy;
using Backend.Infrastructure.Configuration.Options;
using Backend.Infrastructure.Multitenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Backend.Infrastructure.Persistence;

/// <summary>
/// Registers the data-access stack: the EF Core <see cref="AppDbContext"/> (Npgsql),
/// the request-scoped <see cref="IBrandContext"/>, and the <see cref="IBrandScope"/>
/// unit-of-work that binds brand scope per transaction. All scoped, so a request or
/// worker job shares one context + one brand binding.
/// </summary>
public static class DataAccessServiceCollectionExtensions
{
    public static IServiceCollection AddDataAccess(this IServiceCollection services)
    {
        services.AddDbContext<AppDbContext>((serviceProvider, options) =>
        {
            // Connection string resolved at build time from the validated Options,
            // not read ad hoc from configuration.
            var connectionString = serviceProvider
                .GetRequiredService<IOptions<DatabaseOptions>>()
                .Value
                .Postgres;

            options.UseNpgsql(connectionString);
        });

        services.AddScoped<IBrandContext, BrandContext>();
        services.AddScoped<IBrandScope, BrandScope>();

        return services;
    }
}
