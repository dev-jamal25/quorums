// Api host entry point. Registers the dependency health checks and maps the
// GET /health surface. Further DI registration (boundary interfaces, Options/Vault,
// EF Core + RLS interceptor, Hangfire client), middleware, and controllers are
// added in the build-order slices.
using Backend.Api.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDependencyHealthChecks(builder.Configuration);

var app = builder.Build();

app.MapDependencyHealthChecks();

app.Run();
