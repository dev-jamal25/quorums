// Api host entry point. Loads Vault KV secrets into configuration (no-op in dev),
// registers the validated Options pattern (fail-fast on missing required config),
// the data-access stack + RLS binding, brand onboarding, controllers with
// FluentValidation edge validation, and the dependency health checks; maps the
// GET /health surface and the controllers.
using Backend.Api.Dtos;
using Backend.Api.HealthChecks;
using Backend.Infrastructure.Configuration;
using Backend.Infrastructure.Jobs;
using Backend.Infrastructure.Onboarding;
using Backend.Infrastructure.Orchestration;
using Backend.Infrastructure.Persistence;
using FluentValidation;
using FluentValidation.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

await builder.Configuration.AddVaultKvSecretsAsync();

builder.Services.AddValidatedAppOptions(builder.Configuration);
builder.Services.AddDataAccess();
builder.Services.AddOnboarding();
builder.Services.AddHangfireJobStore(builder.Configuration);
builder.Services.AddOrchestration();
builder.Services.AddDependencyHealthChecks(builder.Configuration);

builder.Services.AddControllers();

// FluentValidation at the boundary: validators run on model binding and surface
// through [ApiController] as automatic 400 ProblemDetails.
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining<CreateBrandRequestValidator>();

var app = builder.Build();

app.MapDependencyHealthChecks();
app.MapControllers();

app.Run();
