namespace Backend.Core.Domain;

/// <summary>
/// Marker for an entity owned by exactly one brand and protected by Postgres
/// Row-Level Security. Every domain entity except <see cref="Brand"/> implements
/// this; the RLS policy on its table filters by <see cref="BrandId"/> against the
/// transaction-local <c>app.current_brand</c> setting (DL-002, DL-007).
/// </summary>
public interface IBrandScoped
{
    /// <summary>The owning brand. Never set from caller input — bound from auth.</summary>
    Guid BrandId { get; }
}
