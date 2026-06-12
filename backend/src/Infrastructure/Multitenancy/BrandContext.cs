using Backend.Core.Multitenancy;

namespace Backend.Infrastructure.Multitenancy;

/// <summary>
/// Request-scoped implementation of <see cref="IBrandContext"/>. Holds the brand id
/// bound from auth (or from <c>AgentRun.BrandId</c> in a worker job) for the lifetime
/// of the scope. Registered Scoped so each request/job gets its own instance.
/// </summary>
public sealed class BrandContext : IBrandContext
{
    public Guid? CurrentBrandId { get; private set; }

    public bool HasBrand => CurrentBrandId.HasValue;

    public void Bind(Guid brandId)
    {
        if (brandId == Guid.Empty)
        {
            throw new ArgumentException("Brand id must not be empty.", nameof(brandId));
        }

        CurrentBrandId = brandId;
    }

    public Guid RequireBrandId() =>
        CurrentBrandId
        ?? throw new InvalidOperationException(
            "No brand scope is set. Bind IBrandContext before opening a brand scope.");
}
