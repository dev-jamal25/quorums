using Backend.Core.Orchestration;
using Backend.Core.Storage;
using Backend.Infrastructure.Orchestration.Maf.Nodes;
using Backend.Infrastructure.Tracing;
using Backend.IntegrationTests.Support;
using Xunit;

namespace Backend.IntegrationTests.Durability;

/// <summary>
/// Unit-level tests for the deterministic MAF agent nodes (no DB, no container). Each node
/// must write only its declared <see cref="RunState"/> slice and record its trace span,
/// matching the behaviour the StubOrchestrator had before the graph existed.
/// </summary>
[Trait("Category", "Durability")]
public sealed class MafNodeTests
{
    private static RunState Base(Guid runId, Guid brandId) => new(
        RunId: runId,
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
        Trace: new TraceRefs(TraceId: string.Empty, SpanIds: [], Spans: []));

    [Fact]
    public async Task ContentStrategist_writes_strategy_slice_and_records_span()
    {
        var node = new ContentStrategistExecutor(new LocalTraceRecorder());

        var result = await node.RunAsync(Base(Guid.NewGuid(), Guid.NewGuid()));

        Assert.NotNull(result.Strategy);
        Assert.Equal("stub-pillar", result.Strategy!.Pillar);
        Assert.Equal("stub-objective", result.Strategy.Objective);
        Assert.Contains(result.Trace.Spans, s => s.Node == "strategy" && s.Tool is null && s.Status == "ok");
        // Disjoint ownership: this node touches nothing else.
        Assert.Null(result.Creative);
        Assert.Null(result.Caption);
        Assert.Null(result.Media);
    }

    [Fact]
    public async Task CreativeDirector_writes_creative_slice_and_records_span()
    {
        var node = new CreativeDirectorExecutor(new LocalTraceRecorder());

        var result = await node.RunAsync(Base(Guid.NewGuid(), Guid.NewGuid()));

        Assert.NotNull(result.Creative);
        Assert.Equal("stub-concept", result.Creative!.VisualConcept);
        Assert.Equal("stub-brief", result.Creative.MediaPromptBrief);
        Assert.Contains(result.Trace.Spans, s => s.Node == "creative" && s.Status == "ok");
        Assert.Null(result.Strategy);
        Assert.Null(result.Caption);
    }

    [Fact]
    public async Task Copywriting_writes_caption_slice_and_records_span()
    {
        var node = new CopywritingExecutor(new LocalTraceRecorder());

        var result = await node.RunAsync(Base(Guid.NewGuid(), Guid.NewGuid()));

        Assert.NotNull(result.Caption);
        Assert.Equal("stub-hook", result.Caption!.Hook);
        Assert.Contains("#stub", result.Caption.Hashtags);
        Assert.Contains(result.Trace.Spans, s => s.Node == "copywriting" && s.Status == "ok");
        Assert.Null(result.Media);
    }

    [Fact]
    public async Task MediaGeneration_writes_media_slice_with_deterministic_asset_and_span()
    {
        var runId = Guid.NewGuid();
        var brandId = Guid.NewGuid();
        var node = new MediaGenerationExecutor(new InMemoryStorageService(), new LocalTraceRecorder());

        var result = await node.RunAsync(Base(runId, brandId));

        Assert.NotNull(result.Media);
        Assert.Equal(Backend.Core.Common.DeterministicGuid.From(runId, "asset"), result.Media!.AssetId);
        Assert.Equal(StorageKeys.ForAsset(brandId, result.Media.AssetId, "png"), result.Media.StorageKey);
        Assert.Contains(result.Trace.Spans, s => s.Node == "media" && s.Tool == "minio.put" && s.Status == "ok");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task MediaGeneration_rerun_overwrites_same_key_single_object()
    {
        var runId = Guid.NewGuid();
        var brandId = Guid.NewGuid();
        var storage = new InMemoryStorageService();
        var node = new MediaGenerationExecutor(storage, new LocalTraceRecorder());
        var state = Base(runId, brandId);

        // Simulate a Hangfire retry re-running the media step against the same run.
        await node.RunAsync(state);
        await node.RunAsync(state);

        var keys = await storage.ListAsync(StorageKeys.AssetPrefix(brandId));
        Assert.Single(keys); // idempotent write keyed by deterministic asset id (DL-022)
    }
}
