namespace Backend.Core.Domain;

/// <summary>
/// Metadata for a binary asset whose bytes live in MinIO under
/// <c>brands/{brandId}/assets/{assetId}</c>. The storage key is deterministic so a
/// retried write is idempotent (DL-009, DL-022).
/// </summary>
public sealed class Asset : IBrandScoped
{
    public Guid Id { get; set; }

    public Guid BrandId { get; set; }

    public Guid ContentItemId { get; set; }

    public string StorageKey { get; set; } = default!;

    public string ContentType { get; set; } = default!;

    public DateTimeOffset CreatedAt { get; set; }
}
