// Worker host entry point. Loads Vault KV secrets into configuration (no-op in
// dev) and registers the validated Options pattern (fail-fast on missing required
// config). The Hangfire server, job registration (ExecuteRunJob/ResumeRunJob), and
// the shared Infrastructure DI wiring are added in the build-order slices.
using Backend.Infrastructure.Configuration;
using Backend.Infrastructure.Generation;
using Backend.Infrastructure.Integrations.Meta;
using Backend.Infrastructure.Jobs;
using Backend.Infrastructure.Knowledge;
using Backend.Infrastructure.Orchestration;
using Backend.Infrastructure.Persistence;
using Backend.Infrastructure.Storage;
using Backend.Infrastructure.Tracing;

var builder = Host.CreateApplicationBuilder(args);

await builder.Configuration.AddVaultKvSecretsAsync();

builder.Services.AddValidatedAppOptions(builder.Configuration);
builder.Services.AddDataAccess();
builder.Services.AddKnowledge(builder.Configuration);
builder.Services.AddHangfireJobStore(builder.Configuration);
builder.Services.AddHangfireWorker();
builder.Services.AddStorage();
builder.Services.AddMetaIntegration();
builder.Services.AddTracing(builder.Configuration);
builder.Services.AddGeneration(builder.Configuration);
builder.Services.AddOrchestration();

var host = builder.Build();

host.Run();
