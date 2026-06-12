// Worker host entry point. Loads Vault KV secrets into configuration (no-op in
// dev) and registers the validated Options pattern (fail-fast on missing required
// config). The Hangfire server, job registration (ExecuteRunJob/ResumeRunJob), and
// the shared Infrastructure DI wiring are added in the build-order slices.
using Backend.Infrastructure.Configuration;
using Backend.Infrastructure.Jobs;
using Backend.Infrastructure.Orchestration;
using Backend.Infrastructure.Persistence;

var builder = Host.CreateApplicationBuilder(args);

await builder.Configuration.AddVaultKvSecretsAsync();

builder.Services.AddValidatedAppOptions(builder.Configuration);
builder.Services.AddDataAccess();
builder.Services.AddHangfireJobStore(builder.Configuration);
builder.Services.AddHangfireWorker();
builder.Services.AddOrchestration();

var host = builder.Build();

host.Run();
