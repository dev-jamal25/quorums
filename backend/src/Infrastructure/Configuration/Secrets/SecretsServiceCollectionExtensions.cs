using Backend.Core.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Infrastructure.Configuration.Secrets;

public static class SecretsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ISecretsProvider"/> by mode, gated EXACTLY like the Vault KV loader
    /// (DL-011): real Vault Transit crypto when <c>Vault:Enabled</c> is true, else a dev passthrough
    /// (CI/dev, Vault-free). <c>GetValue&lt;bool&gt;</c> treats a missing/empty key as false and never
    /// throws. Singleton — both providers are stateless.
    /// </summary>
    public static IServiceCollection AddSecrets(this IServiceCollection services, IConfiguration configuration)
    {
        if (configuration.GetValue<bool>("Vault:Enabled", false))
        {
            services.AddSingleton<ISecretsProvider, VaultTransitSecretsProvider>();
        }
        else
        {
            services.AddSingleton<ISecretsProvider, PassthroughSecretsProvider>();
        }

        return services;
    }
}
