// Worker host entry point. Scaffold only: builds and runs an empty generic host.
// The Hangfire server, job registration (ExecuteRunJob/ResumeRunJob), and the
// shared Infrastructure DI wiring are added in the build-order slices.
var builder = Host.CreateApplicationBuilder(args);

var host = builder.Build();

host.Run();
