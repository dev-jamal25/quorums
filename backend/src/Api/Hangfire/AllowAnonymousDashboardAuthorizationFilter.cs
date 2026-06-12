using Hangfire.Dashboard;

namespace Backend.Api.Hangfire;

/// <summary>
/// Dev-only: allows any caller to see the Hangfire dashboard. The default
/// Hangfire filter is local-only, which inside Docker rejects requests from the
/// host (they arrive non-loopback) and returns 404. Replace with real auth +
/// IP allowlist before any non-local environment.
/// </summary>
public sealed class AllowAnonymousDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context) => true;
}
