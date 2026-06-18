using Backend.Core.Orchestration;
using Backend.Infrastructure.Integrations.Meta;
using Backend.Infrastructure.Orchestration.Maf;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Infrastructure.Orchestration;

public static class OrchestrationServiceCollectionExtensions
{
    public static IServiceCollection AddOrchestration(this IServiceCollection services)
    {
        // The robust two-step publish coordinator (DL-038/039): scoped, since it uses the
        // brand-scoped AppDbContext + IBrandScope. The publish node delegates to it.
        services.AddScoped<PublishCoordinator>();

        // The real Microsoft Agent Framework supervised graph (DL-018) behind the same
        // IOrchestrator seam; the durable checkpoint/exit/resume stays in the Hangfire jobs.
        services.AddScoped<IOrchestrator, MafOrchestrator>();
        return services;
    }
}
