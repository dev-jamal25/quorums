// Api host entry point. Loads Vault KV secrets into configuration (no-op in dev),
// registers the validated Options pattern (fail-fast on missing required config),
// the data-access stack + RLS binding, brand onboarding, controllers with
// FluentValidation edge validation, and the dependency health checks; maps the
// GET /health surface and the controllers.
using Backend.Api.Dtos;
using Backend.Api.Hangfire;
using Backend.Api.HealthChecks;
using Backend.Api.Middleware;
using Backend.Infrastructure.Configuration;
using Backend.Infrastructure.Configuration.Secrets;
using Backend.Infrastructure.Generation;
using Backend.Infrastructure.Integrations.Meta;
using Backend.Infrastructure.Jobs;
using Backend.Infrastructure.Knowledge;
using Backend.Infrastructure.Onboarding;
using Backend.Infrastructure.Orchestration;
using Backend.Infrastructure.Persistence;
using Backend.Infrastructure.Storage;
using Backend.Infrastructure.Tracing;
using FluentValidation;
using FluentValidation.AspNetCore;
using Hangfire;

var builder = WebApplication.CreateBuilder(args);

await builder.Configuration.AddVaultKvSecretsAsync();

builder.Services.AddValidatedAppOptions(builder.Configuration);
builder.Services.AddSecrets(builder.Configuration);
builder.Services.AddDataAccess();
builder.Services.AddOnboarding();
builder.Services.AddKnowledge(builder.Configuration);
builder.Services.AddHangfireJobStore(builder.Configuration);
builder.Services.AddStorage();
builder.Services.AddMetaIntegration();
builder.Services.AddTracing(builder.Configuration);
builder.Services.AddGeneration(builder.Configuration);
builder.Services.AddOrchestration();
builder.Services.AddDependencyHealthChecks(builder.Configuration);

builder.Services.AddControllers();

// FluentValidation at the boundary: validators run on model binding and surface
// through [ApiController] as automatic 400 ProblemDetails.
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining<CreateBrandRequestValidator>();

var app = builder.Build();

app.UseMiddleware<BrandContextMiddleware>();

// Dev-only: allows Docker-host browsers to reach the dashboard. The default
// Hangfire filter is local-only, which rejects non-loopback (host→container)
// requests. Replace with real auth before any non-local environment.
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = [new AllowAnonymousDashboardAuthorizationFilter()]
});

app.MapDependencyHealthChecks();
app.MapControllers();

app.Run();
