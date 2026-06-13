using Backend.Core.Integrations;
using Backend.Infrastructure.Configuration.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Backend.Infrastructure.Integrations.Meta;

public static class MetaIntegrationServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IMetaIntegration"/> by mode (Singleton — stateless). The
    /// default <c>Meta:Mode=mock</c> selects the network-free mock; <c>live</c>
    /// selects the not-yet-implemented seam. Selection is resolved once from the
    /// validated <see cref="MetaOptions"/>.
    /// </summary>
    public static IServiceCollection AddMetaIntegration(this IServiceCollection services)
    {
        services.AddSingleton<IMetaIntegration>(sp =>
        {
            var mode = sp.GetRequiredService<IOptions<MetaOptions>>().Value.Mode;
            return mode.Trim().ToLowerInvariant() switch
            {
                "mock" => new MockMetaIntegration(),
                "live" => new LiveMetaIntegration(),
                _ => throw new InvalidOperationException(
                    $"Unknown Meta:Mode '{mode}'. Expected 'mock' or 'live'."),
            };
        });

        return services;
    }
}
