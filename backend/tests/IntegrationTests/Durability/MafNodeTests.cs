using Backend.Core.Orchestration;
using Backend.Core.Storage;
using Backend.Infrastructure.Orchestration.Maf;
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

    [Fact]
    public async Task SupervisorEntry_sets_strategy_phase_and_records_no_span()
    {
        var node = new SupervisorEntryExecutor();
        var input = Base(Guid.NewGuid(), Guid.NewGuid()) with { Phase = GraphPhase.Done };

        var result = await node.HandleAsync(input, context: null!, CancellationToken.None);

        Assert.Equal(GraphPhase.Strategy, result.Phase);
        Assert.Empty(result.Trace.Spans);
    }

    /// <summary>
    /// Runs strategy → creative sequentially, then forks copywriting and media off the same
    /// post-creative state — exactly what the fan-out delivers to both branches.
    /// </summary>
    private static async Task<(RunState Copy, RunState Media)> ForkBranchesAsync(Guid runId, Guid brandId)
    {
        var trace = new LocalTraceRecorder();
        var s0 = Base(runId, brandId);
        s0 = await new ContentStrategistExecutor(trace).RunAsync(s0);
        s0 = await new CreativeDirectorExecutor(trace).RunAsync(s0);

        var copy = await new CopywritingExecutor(trace).RunAsync(s0);
        var media = await new MediaGenerationExecutor(new InMemoryStorageService(), trace).RunAsync(s0);
        return (copy, media);
    }

    [Fact]
    public async Task Assembly_merges_fork_branches_into_awaiting_approval_draft_with_unioned_trace()
    {
        var (copy, media) = await ForkBranchesAsync(Guid.NewGuid(), Guid.NewGuid());

        var merged = AssemblyMerge.Fold(null, copy);
        merged = AssemblyMerge.Fold(merged, media);

        Assert.Equal(GraphPhase.AwaitingApproval, merged.Phase);
        Assert.NotNull(merged.Caption);
        Assert.NotNull(merged.Media);
        Assert.NotNull(merged.Draft);
        Assert.Equal("pending", merged.Draft!.Status);

        // Four unioned spans (strategy, creative, copywriting, media), no duplicates, and the
        // id list stays in lockstep with the detail list (the trace surface contract).
        Assert.Equal(4, merged.Trace.Spans.Count);
        Assert.Equal(merged.Trace.Spans.Count, merged.Trace.SpanIds.Count);
        Assert.Equal(merged.Trace.Spans.Count, merged.Trace.Spans.Select(s => s.SpanId).Distinct().Count());
        Assert.Contains(merged.Trace.Spans, s => s.Node == "strategy");
        Assert.Contains(merged.Trace.Spans, s => s.Node == "media" && s.Tool == "minio.put");
    }

    [Fact]
    public async Task Assembly_merge_is_order_independent()
    {
        var (copy, media) = await ForkBranchesAsync(Guid.NewGuid(), Guid.NewGuid());

        var copyFirst = AssemblyMerge.Fold(AssemblyMerge.Fold(null, copy), media);
        var mediaFirst = AssemblyMerge.Fold(AssemblyMerge.Fold(null, media), copy);

        Assert.Equal(copyFirst.Caption, mediaFirst.Caption);
        Assert.Equal(copyFirst.Media, mediaFirst.Media);
        Assert.Equal(copyFirst.Draft!.Status, mediaFirst.Draft!.Status);
        Assert.Equal(copyFirst.Trace.Spans.Count, mediaFirst.Trace.Spans.Count);
    }
}
