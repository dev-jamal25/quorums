namespace Backend.Core.Multitenancy;

/// <summary>
/// Request-scoped holder of the current brand id, populated from auth at the start
/// of a request (or from <c>AgentRun.BrandId</c> inside a worker job). It is the
/// single source of brand scope; the binding to Postgres happens in
/// <see cref="IBrandScope"/>. Never populated from caller-supplied input.
/// </summary>
public interface IBrandContext
{
    /// <summary>The bound brand id, or <c>null</c> when no brand scope is set.</summary>
    Guid? CurrentBrandId { get; }

    /// <summary>True when a brand id has been bound for this scope.</summary>
    bool HasBrand { get; }

    /// <summary>Binds the brand id for this scope. Throws on <see cref="Guid.Empty"/>.</summary>
    void Bind(Guid brandId);

    /// <summary>
    /// Returns the bound brand id, or throws <see cref="InvalidOperationException"/>
    /// if none is set — there is no implicit "all brands" scope.
    /// </summary>
    Guid RequireBrandId();
}
