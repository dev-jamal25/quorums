// Api host entry point. Loads Vault KV secrets into configuration (no-op in dev),
// registers the validated Options pattern (fail-fast on missing required config),
// and the dependency health checks; maps the GET /health surface. Further DI
// (boundary interfaces, EF Core + RLS interceptor, Hangfire client), middleware,
// and controllers are added in the build-order slices.
using Backend.Api.HealthChecks;
using Backend.Infrastructure.Configuration;
using Backend.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

await builder.Configuration.AddVaultKvSecretsAsync();

builder.Services.AddValidatedAppOptions(builder.Configuration);
builder.Services.AddDataAccess();
builder.Services.AddDependencyHealthChecks(builder.Configuration);

var app = builder.Build();

app.MapDependencyHealthChecks();

app.Run();
