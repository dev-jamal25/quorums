using Backend.Core.Common;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Backend.Core.Storage;
using Backend.Infrastructure.Configuration.Options;
using Backend.Infrastructure.Integrations.Gemini;
using Backend.IntegrationTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Backend.IntegrationTests.Generation;

/// <summary>
/// DL-058 Slice-A generation proofs at the orchestrator level (deterministic clients, no spend): a Veo
/// poll timeout and a video-budget breach both degrade to a caption-only draft that still reaches the
/// gate (no hang, no crash, no fatal). The submit-or-resume idempotency proof lives in the unit-level
/// <c>VeoVideoGeneratorTests</c>. The image path is asserted untouched with no Veo backend present.
/// </summary>
[Trait("Category", "Generation")]
public sealed class VideoGenerationTests
{
    private static VeoVideoGenerator NeverCompletingGenerator(VeoOperationStore store)
    {
        var options = Options.Create(new VeoOptions
        {
            Model = "veo-test",
            MaxDurationSec = 5,
            PollTimeout = TimeSpan.FromMilliseconds(80),
            PollInterval = TimeSpan.FromMilliseconds(5),
        });
        return new VeoVideoGenerator(
            new FakeVeoClient(neverCompletes: true), store, options, NullLogger<VeoVideoGenerator>.Instance);
    }

    [Fact]
    public async Task Veo_poll_timeout_degrades_to_a_caption_only_draft_at_the_gate()
    {
        var store = new VeoOperationStore();
        var tool = new VeoBackedMediaTool(NeverCompletingGenerator(store));
        var orchestrator = TestGeneration.Orchestrator(TestGeneration.Deps(media: tool, veoStore: store));

        var state = await orchestrator.RunGenerationAsync(
            TestGeneration.VideoSeed(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Null(state.FatalError);                       // degrade, not fail
        Assert.Equal(GraphPhase.AwaitingApproval, state.Phase);
        Assert.Null(state.Media);                            // caption-only
        Assert.NotNull(state.Caption);
        Assert.NotNull(state.Draft);
        Assert.Null(state.Draft!.MediaRef);                  // caption-only draft reaches the gate
        Assert.Contains(state.Errors, e => e.Code == "media.generation_failed");
        Assert.Contains(state.Trace.Spans, s => s.Node == "media" && s.Tool == "veo.generate" && s.Status == "degraded");
    }

    [Fact]
    public async Task Video_budget_breach_degrades_to_caption_only_with_zero_veo_calls()
    {
        var media = new RecordingMediaGenerationTool();
        // Price one second above the whole budget so any clip is unaffordable at the pre-Media gate.
        var deps = TestGeneration.Deps(media: media, videoPricePerSec: 1.00m);
        var orchestrator = TestGeneration.Orchestrator(deps);

        var seed = TestGeneration.VideoSeed(
            Guid.NewGuid(),
            Guid.NewGuid(),
            budget: new Budget(TokenBudget: 10_000, TokensSpent: 0, MediaBudget: 0.01m, MediaSpent: 0m));

        var state = await orchestrator.RunGenerationAsync(seed);

        Assert.Null(state.FatalError);
        Assert.Null(state.Media);
        Assert.Equal(0, media.Calls);                        // gate tripped before any Veo job
        Assert.NotNull(state.Caption);
        Assert.NotNull(state.Draft);
        Assert.Null(state.Draft!.MediaRef);
        Assert.Contains(
            state.Trace.Spans,
            s => s.Node == "media" && s.Status == "degraded"
                && (s.Detail ?? string.Empty).Contains("video budget exhausted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Veo_terminal_error_degrades_to_a_caption_only_draft_at_the_gate()
    {
        // Terminal Veo error (4xx / content-policy / 5xx-after-retries) is a DIFFERENT code path from a
        // poll timeout, but degrades the same way — caption-only, never an exception into the graph.
        var store = new VeoOperationStore();
        var options = Options.Create(new VeoOptions
        {
            Model = "veo-test",
            MaxDurationSec = 5,
            PollTimeout = TimeSpan.FromSeconds(5),
            PollInterval = TimeSpan.FromMilliseconds(5),
        });
        var generator = new VeoVideoGenerator(
            new FakeVeoClient(failTerminally: true), store, options, NullLogger<VeoVideoGenerator>.Instance);
        var orchestrator = TestGeneration.Orchestrator(
            TestGeneration.Deps(media: new VeoBackedMediaTool(generator), veoStore: store));

        var state = await orchestrator.RunGenerationAsync(
            TestGeneration.VideoSeed(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Null(state.FatalError);                       // degrade, not fail
        Assert.Equal(GraphPhase.AwaitingApproval, state.Phase);
        Assert.Null(state.Media);                            // caption-only
        Assert.NotNull(state.Draft);
        Assert.Null(state.Draft!.MediaRef);
        Assert.Contains(state.Errors, e => e.Code == "media.generation_failed");
        Assert.Contains(state.Trace.Spans, s => s.Node == "media" && s.Tool == "veo.generate" && s.Status == "degraded");
    }

    [Fact]
    public async Task Existing_asset_is_reused_with_zero_veo_submits_and_zero_image_generations()
    {
        var runId = Guid.NewGuid();
        var brandId = Guid.NewGuid();
        var assetId = DeterministicGuid.From(runId, "asset");
        var storage = new InMemoryStorageService();

        // Simulate a prior successful generation: the deterministic mp4 asset is already committed (its
        // in-flight Veo op long evicted). A whole-ExecuteRun retry must REUSE it — not re-bill a Veo job.
        await storage.PutAsync(StorageKeys.ForAsset(brandId, assetId, "mp4"), [0x00, 0x00, 0x00, 0x18], "video/mp4");

        // RecordingMediaGenerationTool counts EVERY node→tool entry; the tool is the single entry point for
        // BOTH the ImageSeed image sub-call and the Veo submit. Calls == 0 ⇒ zero of each.
        var media = new RecordingMediaGenerationTool();
        var orchestrator = TestGeneration.Orchestrator(TestGeneration.Deps(media: media, storage: storage));

        var state = await orchestrator.RunGenerationAsync(TestGeneration.VideoSeed(runId, brandId));

        Assert.Equal(0, media.Calls);                        // ZERO Veo submits AND zero image generations
        Assert.Null(state.FatalError);
        Assert.Equal(GraphPhase.AwaitingApproval, state.Phase);
        Assert.NotNull(state.Media);
        Assert.Equal(StorageKeys.ForAsset(brandId, assetId, "mp4"), state.Media!.StorageKey);
        Assert.Equal("video/mp4", state.Media.MimeType);
        Assert.Equal("video", state.Media.Modality);
        Assert.NotNull(state.Draft!.MediaRef);               // reused asset assembles into a video draft
        Assert.Contains(state.Trace.Spans, s => s.Node == "media" && s.Tool == "minio.exists" && s.Status == "ok");
    }

    [Fact]
    public async Task Image_run_is_untouched_with_no_veo_backend_present()
    {
        // Default deps wire no Veo backend (the deterministic image tool), mirroring Veo config absent.
        var orchestrator = TestGeneration.Orchestrator(TestGeneration.Deps());

        var state = await orchestrator.RunGenerationAsync(
            TestGeneration.Seed(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Null(state.FatalError);
        Assert.Equal(GraphPhase.AwaitingApproval, state.Phase);
        Assert.NotNull(state.Media);
        Assert.Equal("image", state.Media!.Modality);
        Assert.Equal("image/png", state.Media.MimeType);
        Assert.Null(state.Media.DurationSec);
        Assert.Empty(state.Errors);
    }
}
