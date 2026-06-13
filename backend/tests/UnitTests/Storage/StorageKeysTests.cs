using Backend.Core.Common;
using Backend.Core.Storage;
using Xunit;

namespace Backend.UnitTests.Storage;

public sealed class StorageKeysTests
{
    [Fact]
    public void ForAsset_is_brand_prefixed_and_carries_extension()
    {
        var brandId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var assetId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var key = StorageKeys.ForAsset(brandId, assetId, "png");

        Assert.Equal(
            "brands/11111111-1111-1111-1111-111111111111/assets/22222222-2222-2222-2222-222222222222.png",
            key);
    }

    [Fact]
    public void ForAsset_normalizes_a_leading_dot_on_the_extension()
    {
        var brandId = Guid.NewGuid();
        var assetId = Guid.NewGuid();

        var withDot = StorageKeys.ForAsset(brandId, assetId, ".png");
        var withoutDot = StorageKeys.ForAsset(brandId, assetId, "png");

        Assert.Equal(withoutDot, withDot);
    }

    [Fact]
    public void Two_brands_never_share_a_prefix()
    {
        var brandA = Guid.NewGuid();
        var brandB = Guid.NewGuid();

        Assert.StartsWith(StorageKeys.BrandPrefix(brandA), StorageKeys.AssetPrefix(brandA));
        Assert.NotEqual(StorageKeys.BrandPrefix(brandA), StorageKeys.BrandPrefix(brandB));
        Assert.DoesNotContain(brandB.ToString(), StorageKeys.AssetPrefix(brandA));
    }

    [Fact]
    public void DeterministicGuid_is_stable_for_the_same_seed_and_purpose()
    {
        var seed = Guid.NewGuid();

        Assert.Equal(DeterministicGuid.From(seed, "asset"), DeterministicGuid.From(seed, "asset"));
    }

    [Fact]
    public void DeterministicGuid_differs_by_purpose_and_by_seed()
    {
        var seed = Guid.NewGuid();
        var other = Guid.NewGuid();

        Assert.NotEqual(DeterministicGuid.From(seed, "asset"), DeterministicGuid.From(seed, "meta"));
        Assert.NotEqual(DeterministicGuid.From(seed, "asset"), DeterministicGuid.From(other, "asset"));
    }
}
