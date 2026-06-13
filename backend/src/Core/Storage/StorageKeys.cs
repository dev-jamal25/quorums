namespace Backend.Core.Storage;

/// <summary>
/// The single owner of the object-storage key scheme (DL-009). Every asset lives at
/// <c>brands/{brandId}/assets/{assetId}.{ext}</c>. The brand prefix is the storage
/// analogue of the RLS <c>brand_id</c> filter: it is structural isolation, never a
/// caller-supplied path. Both the orchestrator and the tests build keys through here
/// so the scheme has exactly one definition.
/// </summary>
public static class StorageKeys
{
    /// <summary>Prefix that scopes every object owned by a brand.</summary>
    public static string BrandPrefix(Guid brandId) => $"brands/{brandId}/";

    /// <summary>Prefix that scopes a brand's media assets.</summary>
    public static string AssetPrefix(Guid brandId) => $"{BrandPrefix(brandId)}assets/";

    /// <summary>Deterministic key for a single asset, including a file extension.</summary>
    public static string ForAsset(Guid brandId, Guid assetId, string extension)
    {
        var ext = extension.TrimStart('.');
        return $"{AssetPrefix(brandId)}{assetId}.{ext}";
    }
}
