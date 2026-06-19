using Backend.Core.Integrations;
using Backend.Infrastructure.Configuration.Options;
using Backend.Infrastructure.Integrations.Meta;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Backend.UnitTests.Integrations;

/// <summary>
/// DL-055 / DL-051 config-gate proof: the live Meta path cannot fire under CI/default config. The
/// DI-resolved <see cref="IMetaIntegration"/> is the network-free <see cref="MockMetaIntegration"/>
/// unless <c>Meta:Mode=live</c> is explicitly set — only then is the real
/// <see cref="LiveMetaIntegration"/> resolved (and even then, merely constructing it makes no HTTP
/// call). The full suite runs under default config, so no live Meta call is ever made in CI.
/// </summary>
public sealed class MetaIntegrationModeTests
{
    private static IMetaIntegration Resolve(string? mode)
    {
        var settings = new Dictionary<string, string?>
        {
            [$"{MetaOptions.SectionName}:GraphBaseUrl"] = "https://graph.facebook.com",
        };
        if (mode is not null)
        {
            settings[$"{MetaOptions.SectionName}:Mode"] = mode;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddOptions<MetaOptions>().Bind(configuration.GetSection(MetaOptions.SectionName));
        services.AddMetaIntegration(configuration);

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IMetaIntegration>();
    }

    [Fact]
    public void Default_config_resolves_the_mock() =>
        Assert.IsType<MockMetaIntegration>(Resolve(mode: null));

    [Fact]
    public void Mock_mode_resolves_the_mock() =>
        Assert.IsType<MockMetaIntegration>(Resolve("mock"));

    [Fact]
    public void Live_mode_is_the_only_way_to_resolve_the_live_client() =>
        Assert.IsType<LiveMetaIntegration>(Resolve("live"));
}
