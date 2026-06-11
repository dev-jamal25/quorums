// Api host entry point. Scaffold only: builds and runs an empty ASP.NET Core host.
// DI registration (boundary interfaces, Options/Vault, EF Core + RLS interceptor,
// Hangfire client), middleware, and controllers are added in the build-order slices.
var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.Run();
