namespace Backend.Infrastructure.Configuration.Options;

/// <summary>
/// Non-secret storage settings that are not MinIO-client config. <see cref="PublicBaseUrl"/> is the
/// Meta-reachable public origin the live publish path prepends to a brand-prefixed asset key to form
/// the <c>MediaUrl</c> Meta fetches server-side (DL-055): <c>{PublicBaseUrl}/brands/{brandId}/assets/
/// {assetId}.png</c>. Empty by default — the mock ignores <c>MediaUrl</c>, so CI/compose never need it;
/// the live runbook sets it to the public tunnel host. Optional (no <c>[Required]</c>) so mock mode
/// never fails startup validation.
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string PublicBaseUrl { get; init; } = string.Empty;
}
