// Worker host entry point. Loads Vault KV secrets into configuration (no-op in
// dev) and registers the validated Options pattern (fail-fast on missing required
// config). The Hangfire server, job registration (ExecuteRunJob/ResumeRunJob), and
// the shared Infrastructure DI wiring are added in the build-order slices.
using Backend.Infrastructure.Configuration;
using Backend.Infrastructure.Configuration.Secrets;
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
builder.Services.AddSecrets(builder.Configuration);
builder.Services.AddDataAccess();
builder.Services.AddKnowledge(builder.Configuration);
// The Worker is the SOLE Hangfire schema installer (the api uses-but-does-not-install), so the
// concurrent CREATE SCHEMA "hangfire" race cannot occur.
builder.Services.AddHangfireJobStore(builder.Configuration, installSchema: true);
builder.Services.AddHangfireWorker();
builder.Services.AddStorage();
builder.Services.AddMetaIntegration();
builder.Services.AddTracing(builder.Configuration);
builder.Services.AddGeneration(builder.Configuration);
builder.Services.AddOrchestration();

var host = builder.Build();

// Schema-readiness sentinel for the api's depends_on healthcheck. By ApplicationStarted the Hangfire
// server has started, which means PostgreSqlObjectsInstaller has created the hangfire schema + tables
// (installSchema: true above). Dropping this file is the signal the compose healthcheck tests, so the
// api is only released once the schema actually exists.
var readinessFile = Path.Combine(Path.GetTempPath(), "hangfire-ready");
host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.Register(() =>
    File.WriteAllText(readinessFile, DateTimeOffset.UtcNow.ToString("O")));

host.Run();
