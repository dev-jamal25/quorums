using Backend.Core.Orchestration;
using Backend.Infrastructure.Orchestration.Maf;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Infrastructure.Orchestration;

public static class OrchestrationServiceCollectionExtensions
{
    public static IServiceCollection AddOrchestration(this IServiceCollection services)
    {
        // The real Microsoft Agent Framework supervised graph (DL-018) behind the same
        // IOrchestrator seam; the durable checkpoint/exit/resume stays in the Hangfire jobs.
        services.AddScoped<IOrchestrator, MafOrchestrator>();
        return services;
    }
}
