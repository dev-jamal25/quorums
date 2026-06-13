using Backend.Core.Common;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Backend.Core.Storage;
using Backend.Infrastructure.Orchestration;
using Xunit;

namespace Backend.IntegrationTests.Storage;

/// <summary>
/// Exercises the real media-write seam (the write ExecuteRun performs during
/// generation) against a MinIO container: brand-prefixed key, object actually
/// present, prefix isolation between brands, and idempotency under retry.
/// </summary>
[Trait("Category", "Storage")]
public sealed class StorageTests : IClassFixture<MinioFixture>
{
    private readonly MinioFixture _fixture;

    public StorageTests(MinioFixture fixture) => _fixture = fixture;

    private static RunState NewState(Guid brandId) => new(
        RunId: Guid.NewGuid(),
        BrandId: brandId,
        Phase: GraphPhase.Strategy,
        Strategy: null,
        Creative: null,
        Caption: null,
        Media: null,
        Draft: null,
        Approval: null,
        Publish: null,
        Budget: new Budget(TokenBudget: 10_000, TokensSpent: 0, MediaBudget: 1.00m, MediaSpent: 0m),
        Errors: [],
        Trace: new TraceRefs(TraceId: string.Empty, SpanIds: []));

    [Fact]
    public async Task Generation_writes_a_brand_prefixed_object_that_exists_in_storage()
    {
        var brandId = Guid.NewGuid();
        var state = NewState(brandId);
        var orchestrator = new StubOrchestrator(_fixture.Storage);

        var result = await orchestrator.RunGenerationAsync(state);

        Assert.NotNull(result.Media);
        Assert.StartsWith(StorageKeys.AssetPrefix(brandId), result.Media!.StorageKey);
        Assert.True(await _fixture.Storage.ExistsAsync(result.Media.StorageKey));
        Assert.Empty(result.Errors);
    }

    [Fact]
    [Trait("Category", "Isolation")]
    public async Task Object_written_for_brand_A_is_not_visible_under_brand_B_prefix()
    {
        var brandA = Guid.NewGuid();
        var brandB = Guid.NewGuid();
        var orchestrator = new StubOrchestrator(_fixture.Storage);

        var result = await orchestrator.RunGenerationAsync(NewState(brandA));

        var underA = await _fixture.Storage.ListAsync(StorageKeys.BrandPrefix(brandA));
        var underB = await _fixture.Storage.ListAsync(StorageKeys.BrandPrefix(brandB));

        Assert.Contains(result.Media!.StorageKey, underA);
        Assert.DoesNotContain(result.Media.StorageKey, underB);
        Assert.Empty(underB);
    }

    [Fact]
    public async Task Re_running_generation_for_the_same_run_writes_no_duplicate_object()
    {
        var brandId = Guid.NewGuid();
        var state = NewState(brandId);
        var orchestrator = new StubOrchestrator(_fixture.Storage);

        var first = await orchestrator.RunGenerationAsync(state);
        var second = await orchestrator.RunGenerationAsync(state);

        // Same run id → deterministic asset id → identical key → overwrite, not duplicate.
        Assert.Equal(first.Media!.StorageKey, second.Media!.StorageKey);

        var assetId = DeterministicGuid.From(state.RunId, "asset");
        var expectedKey = StorageKeys.ForAsset(brandId, assetId, "png");
        var underAssetPrefix = await _fixture.Storage.ListAsync(StorageKeys.AssetPrefix(brandId));

        Assert.Equal(expectedKey, first.Media.StorageKey);
        Assert.Single(underAssetPrefix);
    }
}
