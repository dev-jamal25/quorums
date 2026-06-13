using System.Net.Http.Headers;
using System.Text;
using Backend.Core.Orchestration;
using Backend.Infrastructure.Configuration.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Infrastructure.Tracing;

public static class TracingServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ITrace"/>. When Langfuse is configured (BaseUrl + both
    /// keys present) the typed-client <see cref="LangfuseTrace"/> is used; otherwise
    /// tracing degrades to the in-process <see cref="LocalTraceRecorder"/>. Langfuse
    /// is optional and config-gated exactly like Vault — its absence never fails a run.
    /// </summary>
    public static IServiceCollection AddTracing(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var langfuse = configuration.GetSection(LangfuseOptions.SectionName).Get<LangfuseOptions>()
            ?? new LangfuseOptions();

        if (!langfuse.IsConfigured)
        {
            services.AddSingleton<ITrace, LocalTraceRecorder>();
            return services;
        }

        services.AddHttpClient<ITrace, LangfuseTrace>(client =>
        {
            client.BaseAddress = new Uri($"{langfuse.BaseUrl!.TrimEnd('/')}/");
            var token = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{langfuse.PublicKey}:{langfuse.SecretKey}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
            client.Timeout = TimeSpan.FromSeconds(5);
        });

        return services;
    }
}
