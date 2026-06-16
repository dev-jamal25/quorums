using Backend.Core.Common;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Backend.Core.Storage;
using Backend.Infrastructure.Integrations.Meta;
using Backend.Infrastructure.Orchestration.Maf;
using Backend.Infrastructure.Orchestration.Maf.Nodes;
using Backend.Infrastructure.Tracing;
using Backend.IntegrationTests.Support;
using Xunit;

namespace Backend.IntegrationTests.Durability;

/// <summary>
/// Node-level tests for the real MAF agent nodes (no DB, deterministic CI mocks). Each node writes
/// only its declared <see cref="RunState"/> slice, records its span, and (for Media) gates on the
/// budget before any tool call. The full-pipeline proofs live in the Generation suite.
/// </summary>
[Trait("Category", "Durability")]
public sealed class MafNodeTests
{
    private static readonly string[] _pillars = ["Origin", "Craft", "Ritual"];

    private static RunState Base(Guid runId, Guid brandId) => TestGeneration.Seed(runId, brandId);

    [Fact]
    public async Task Strategist_emits_three_validated_candidates()
    {
        var deps = TestGeneration.Deps();
        var result = await new ContentStrategistExecutor(deps).RunAsync(Base(Guid.NewGuid(), Guid.NewGuid()));

        Assert.NotNull(result.Candidates);
        Assert.Equal(3, result.Candidates!.Candidates.Count);
        Assert.All(result.Candidates.Candidates, candidate => Assert.Contains(candidate.Pillar, _pillars));
        Assert.Contains(result.Trace.Spans, s => s.Node == "strategy" && s.Status == "ok");
        Assert.Null(result.Strategy);   // selection sets the chosen strategy, not the strategist
        Assert.Null(result.FatalError);
    }

    [Fact]
    public async Task Selection_sets_the_chosen_strategy_and_records_candidates_in_the_span_detail()
    {
        var deps = TestGeneration.Deps();
        var afterStrategy = await new ContentStrategistExecutor(deps).RunAsync(Base(Guid.NewGuid(), Guid.NewGuid()));

        var result = await new SupervisorSelectionExecutor(deps).RunAsync(afterStrategy);

        Assert.NotNull(result.Strategy);
        var span = Assert.Single(result.Trace.Spans, s => s.Node == "supervisor-selection");
        Assert.NotNull(span.Detail);
        Assert.Contains("chosenIndex", span.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreativeDirector_stamps_the_surface_aspect_ratio_over_the_model_value()
    {
        var deps = TestGeneration.Deps();
        var postSelection = await PostSelectionAsync(deps, Guid.NewGuid(), Guid.NewGuid());

        var result = await new CreativeDirectorExecutor(deps).RunAsync(postSelection);

        Assert.NotNull(result.Creative);
        // The mock brief asked for "16:9"; the feed surface allows 4:5 | 1:1, canonical = 4:5 (R8).
        Assert.Equal("4:5", result.Creative!.MediaPromptBrief.AspectRatio);
        Assert.Contains(result.Trace.Spans, s => s.Node == "creative" && s.Status == "ok");
    }

    [Fact]
    public async Task Copywriting_writes_the_caption_slice()
    {
        var deps = TestGeneration.Deps();
        var postCreative = await PostCreativeAsync(deps, Guid.NewGuid(), Guid.NewGuid());

        var result = await new CopywritingExecutor(deps).RunAsync(postCreative);

        Assert.NotNull(result.Caption);
        Assert.NotEmpty(result.Caption!.Hook);
        Assert.NotEmpty(result.Caption.Hashtags);
        Assert.Contains(result.Trace.Spans, s => s.Node == "copywriting" && s.Status == "ok");
    }

    [Fact]
    public async Task MediaGeneration_writes_a_deterministic_asset_when_affordable()
    {
        var runId = Guid.NewGuid();
        var brandId = Guid.NewGuid();
        var media = new RecordingMediaGenerationTool();
        var deps = TestGeneration.Deps(media: media);
        var postCreative = await PostCreativeAsync(deps, runId, brandId);

        var result = await new MediaGenerationExecutor(deps).RunAsync(postCreative);

        Assert.NotNull(result.Media);
        Assert.Equal(DeterministicGuid.From(runId, "asset"), result.Media!.AssetId);
        Assert.Equal(StorageKeys.ForAsset(brandId, result.Media.AssetId, "png"), result.Media.StorageKey);
        Assert.Equal(1, media.Calls);
        Assert.Contains(result.Trace.Spans, s => s.Node == "media" && s.Tool == "gemini.generate");
        Assert.Contains(result.Trace.Spans, s => s.Node == "media" && s.Tool == "minio.put");
        Assert.Null(result.FatalError);
    }

    [Fact]
    public async Task MediaGeneration_rerun_overwrites_the_same_key_single_object()
    {
        var runId = Guid.NewGuid();
        var brandId = Guid.NewGuid();
        var storage = new InMemoryStorageService();
        var deps = TestGeneration.Deps(storage: storage);
        var postCreative = await PostCreativeAsync(deps, runId, brandId);

        await new MediaGenerationExecutor(deps).RunAsync(postCreative);
        await new MediaGenerationExecutor(deps).RunAsync(postCreative);

        var keys = await storage.ListAsync(StorageKeys.AssetPrefix(brandId));
        Assert.Single(keys); // idempotent write keyed by deterministic asset id (DL-022)
    }

    [Fact]
    public async Task MediaGeneration_degrades_to_caption_only_with_zero_tool_calls_when_unaffordable()
    {
        var media = new RecordingMediaGenerationTool();
        var deps = TestGeneration.Deps(media: media);
        var postCreative = await PostCreativeAsync(deps, Guid.NewGuid(), Guid.NewGuid());
        var noMediaBudget = postCreative with { Budget = postCreative.Budget with { MediaBudget = 0m } };

        var result = await new MediaGenerationExecutor(deps).RunAsync(noMediaBudget);

        Assert.Null(result.Media);
        Assert.Null(result.FatalError);                 // degrade, not fail (R1)
        Assert.Equal(0, media.Calls);                   // zero Gemini calls before the gate
        Assert.Contains(result.Trace.Spans, s => s.Node == "media" && s.Status == "degraded");
    }

    [Fact]
    public async Task MediaGeneration_fails_fatally_with_zero_tool_calls_when_the_global_ceiling_is_exceeded()
    {
        var media = new RecordingMediaGenerationTool();
        var deps = TestGeneration.Deps(media: media, globalCeilingUsd: 0.0000001m);
        var postCreative = await PostCreativeAsync(deps, Guid.NewGuid(), Guid.NewGuid());

        var result = await new MediaGenerationExecutor(deps).RunAsync(postCreative);

        Assert.NotNull(result.FatalError);
        Assert.Equal("budget.ceiling_exceeded", result.FatalError!.Code);
        Assert.Equal(0, media.Calls);
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

    [Fact]
    public async Task Assembly_merges_fork_branches_into_an_awaiting_approval_draft_and_folds_the_budget()
    {
        var deps = TestGeneration.Deps();
        var (copy, media) = await ForkBranchesAsync(deps, Guid.NewGuid(), Guid.NewGuid());

        var merged = AssemblyMerge.Fold(null, copy);
        merged = AssemblyMerge.Fold(merged, media);

        Assert.Equal(GraphPhase.AwaitingApproval, merged.Phase);
        Assert.NotNull(merged.Caption);
        Assert.NotNull(merged.Media);
        Assert.Equal("pending", merged.Draft!.Status);

        // Five unioned spans (strategy, selection, creative, copywriting, media), ids in lockstep.
        Assert.Contains(merged.Trace.Spans, s => s.Node == "strategy");
        Assert.Contains(merged.Trace.Spans, s => s.Node == "supervisor-selection");
        Assert.Contains(merged.Trace.Spans, s => s.Node == "media");
        Assert.Equal(merged.Trace.Spans.Count, merged.Trace.SpanIds.Count);

        // The Supervisor folded the per-node costs into the budget (R3).
        Assert.True(merged.Budget.TokensSpent > 0);
        Assert.Equal(TestGeneration.Prices().GeminiPerImage, merged.Budget.MediaSpent);
    }

    [Fact]
    public async Task Assembly_merge_is_order_independent()
    {
        var deps = TestGeneration.Deps();
        var (copy, media) = await ForkBranchesAsync(deps, Guid.NewGuid(), Guid.NewGuid());

        var copyFirst = AssemblyMerge.Fold(AssemblyMerge.Fold(null, copy), media);
        var mediaFirst = AssemblyMerge.Fold(AssemblyMerge.Fold(null, media), copy);

        Assert.Equal(copyFirst.Caption, mediaFirst.Caption);
        Assert.Equal(copyFirst.Media, mediaFirst.Media);
        Assert.Equal(copyFirst.Draft!.Status, mediaFirst.Draft!.Status);
        Assert.Equal(copyFirst.Trace.Spans.Count, mediaFirst.Trace.Spans.Count);
    }

    [Fact]
    public async Task Publishing_publishes_via_mock_and_reaches_done()
    {
        var node = new PublishingExecutor(new MockMetaIntegration(), new LocalTraceRecorder());
        var state = Base(Guid.NewGuid(), Guid.NewGuid()) with
        {
            Caption = new Caption("hook", "body", ["#stub"], new Grounding(false, [], Confidence.Low)),
        };

        var result = await node.RunAsync(state);

        Assert.Equal(GraphPhase.Done, result.Phase);
        Assert.StartsWith("mock://meta/", result.Publish!.ExternalRef!);
        Assert.Equal("published", result.Publish.Status);
        Assert.Contains(result.Trace.Spans, s => s.Node == "publishing" && s.Tool == "meta.publish" && s.Status == "ok");
    }

    [Fact]
    public async Task Publishing_external_ref_is_deterministic_across_retries()
    {
        var node = new PublishingExecutor(new MockMetaIntegration(), new LocalTraceRecorder());
        var state = Base(Guid.NewGuid(), Guid.NewGuid()) with
        {
            Caption = new Caption("h", "b", ["#x"], new Grounding(false, [], Confidence.Low)),
        };

        var first = await node.RunAsync(state);
        var second = await node.RunAsync(state);

        Assert.Equal(first.Publish!.ExternalRef, second.Publish!.ExternalRef); // keyed by run id (DL-022)
    }

    [Fact]
    public async Task Stub_nodes_return_not_implemented_marker_and_do_not_throw()
    {
        var ads = await new AdsOptimizationExecutor()
            .HandleAsync(Base(Guid.NewGuid(), Guid.NewGuid()), context: null!, CancellationToken.None);
        Assert.Contains(ads.Errors, e => e.Code == "ads.not_implemented" && !e.Retryable);

        var analytics = await new AnalyticsExecutor()
            .HandleAsync(Base(Guid.NewGuid(), Guid.NewGuid()), context: null!, CancellationToken.None);
        Assert.Contains(analytics.Errors, e => e.Code == "analytics.not_implemented" && !e.Retryable);
    }

    private static async Task<RunState> PostSelectionAsync(GenerationAgentDeps deps, Guid runId, Guid brandId)
    {
        var state = TestGeneration.Seed(runId, brandId);
        state = await new ContentStrategistExecutor(deps).RunAsync(state);
        return await new SupervisorSelectionExecutor(deps).RunAsync(state);
    }

    private static async Task<RunState> PostCreativeAsync(GenerationAgentDeps deps, Guid runId, Guid brandId)
    {
        var state = await PostSelectionAsync(deps, runId, brandId);
        return await new CreativeDirectorExecutor(deps).RunAsync(state);
    }

    private static async Task<(RunState Copy, RunState Media)> ForkBranchesAsync(
        GenerationAgentDeps deps, Guid runId, Guid brandId)
    {
        var postCreative = await PostCreativeAsync(deps, runId, brandId);
        var copy = await new CopywritingExecutor(deps).RunAsync(postCreative);
        var media = await new MediaGenerationExecutor(deps).RunAsync(postCreative);
        return (copy, media);
    }
}
