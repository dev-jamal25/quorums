namespace Backend.Core.Domain;

/// <summary>
/// The root tenant entity and the unit of isolation. <see cref="Brand"/> itself is
/// NOT brand-scoped (it defines the scope) and therefore carries no RLS policy;
/// every other domain entity references it via <c>BrandId</c> (DL-002, DL-007).
/// </summary>
public sealed class Brand
{
    public Guid Id { get; set; }

    public string Name { get; set; } = default!;

    public DateTimeOffset CreatedAt { get; set; }
}
