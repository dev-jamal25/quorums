using Backend.Core.Orchestration;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Infrastructure.Orchestration;

public static class OrchestrationServiceCollectionExtensions
{
    public static IServiceCollection AddOrchestration(this IServiceCollection services)
    {
        services.AddScoped<IOrchestrator, StubOrchestrator>();
        return services;
    }
}
