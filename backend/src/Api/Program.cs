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

// CORS for the browser dashboard: the Next.js frontend is a different origin and sends a custom
// X-Brand-Id header, which makes the browser issue an OPTIONS preflight the API must answer.
const string FrontendCorsPolicy = "frontend";

await builder.Configuration.AddVaultKvSecretsAsync();

builder.Services.AddValidatedAppOptions(builder.Configuration);
builder.Services.AddSecrets(builder.Configuration);
builder.Services.AddDataAccess();
builder.Services.AddOnboarding();
builder.Services.AddKnowledge(builder.Configuration);
// Api uses the Hangfire store but does NOT install its schema — the Worker is the sole installer
// (single-authority, avoids the concurrent CREATE SCHEMA race). The api's depends_on waits on the
// worker's schema-readiness healthcheck, so the schema exists before the api touches the store.
builder.Services.AddHangfireJobStore(builder.Configuration, installSchema: false);
builder.Services.AddStorage();
builder.Services.AddMetaIntegration();
builder.Services.AddTracing(builder.Configuration);
builder.Services.AddGeneration(builder.Configuration);
builder.Services.AddOrchestration();
builder.Services.AddDependencyHealthChecks(builder.Configuration);

builder.Services.AddControllers();

// Allowed origins are config-driven (Cors:AllowedOrigins), defaulting to the dev frontend URL — never
// a hardcoded literal as the only source. Allow the headers the api-client sends (X-Brand-Id +
// Content-Type) and the methods it uses (GET/POST). No credentials (the client sends no cookies).
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:3000"];
builder.Services.AddCors(options => options.AddPolicy(FrontendCorsPolicy, policy => policy
    .WithOrigins(allowedOrigins)
    .WithHeaders("X-Brand-Id", "Content-Type")
    .WithMethods("GET", "POST")));

// FluentValidation at the boundary: validators run on model binding and surface
// through [ApiController] as automatic 400 ProblemDetails.
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining<CreateBrandRequestValidator>();

var app = builder.Build();

// CORS before the brand-context middleware so the header-less preflight is answered here and never
// needs a brand. After routing, before the endpoints/auth — per ASP.NET Core guidance.
app.UseCors(FrontendCorsPolicy);

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
