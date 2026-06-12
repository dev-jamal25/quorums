using Backend.Core.Multitenancy;

namespace Backend.Api.Middleware;

public sealed class BrandContextMiddleware
{
    private readonly RequestDelegate _next;

    public BrandContextMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IBrandContext brandContext)
    {
        if (context.Request.Headers.TryGetValue("X-Brand-Id", out var headerValue)
            && Guid.TryParse(headerValue, out var brandId))
        {
            brandContext.Bind(brandId);
        }

        await _next(context);
    }
}
