using Backend.Core.Evaluation;
using Backend.Core.Generation.Cost;
using Backend.Core.Generation.PlatformConstraints;
using Backend.Core.Integrations;
using Backend.Core.Knowledge;
using Backend.Core.Multitenancy;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Backend.Core.Secrets;
using Backend.Core.Storage;
using Backend.Infrastructure.Configuration.Secrets;
using Backend.Infrastructure.Generation;
using Backend.Infrastructure.Integrations.Gemini;
using Backend.Infrastructure.Integrations.Meta;
using Backend.Infrastructure.Orchestration.Maf;
using Backend.Infrastructure.Persistence;
using Backend.Infrastructure.Tracing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Backend.IntegrationTests.Support;

/// <summary>
/// Test factories for the generation pipeline: a <see cref="GenerationAgentDeps"/> bundle wired with
/// the deterministic CI mocks (no live Claude/Gemini), and a seeded <see cref="RunState"/>. Knobs let
/// a test force the failure paths (fail/flaky tools, a tiny global ceiling, a tiny media budget,
/// empty retrieval).
/// </summary>
internal static class TestGeneration
{
    public const string SonnetModel = "test-sonnet";
    public const string HaikuModel = "test-haiku";

    public static GenerationAgentDeps Deps(
        IRetrievalService? retrieval = null,
        IStorageService? storage = null,
        IMediaGenerationTool? media = null,
        IEnumerable<string>? failTools = null,
        IEnumerable<string>? flakyTools = null,
        decimal globalCeilingUsd = 1.00m,
        ITrace? trace = null,
        VeoOperationStore? veoStore = null,
        decimal videoPricePerSec = 0.10m,
        int maxVideoDurationSec = 5)
    {
        var chat = new DeterministicGenerationChatClient(failTools, flakyTools);
        return new GenerationAgentDeps(
            Generator: new ForcedToolGenerator(chat),
            Retrieval: retrieval ?? new FakeRetrievalService(),
            Media: media ?? new DeterministicMediaGenerationTool(),
            Storage: storage ?? new InMemoryStorageService(),
            Constraints: Constraints(),
            Prices: Prices(),
            GlobalCeilingUsd: globalCeilingUsd,
            SonnetModel: SonnetModel,
            HaikuModel: HaikuModel,
            Trace: trace ?? new LocalTraceRecorder(),
            LoggerFactory: NullLoggerFactory.Instance,
            VeoStore: veoStore ?? new VeoOperationStore(),
            VideoPricePerSec: videoPricePerSec,
            MaxVideoDurationSec: maxVideoDurationSec);
    }

    /// <summary>
    /// Eval variant: deterministic deps for a mock-mode generation. Per-node grounding provenance now
    /// rides the durable trace (DL-054) — no injected-id recording double — so a test reads injected +
    /// claimed ids from the projected trace. The <see cref="CountingChatClient"/> is retained for the
    /// per-node retry counts (not on the trace). <paramref name="retrieval"/> + <paramref name="groundingClaim"/>
    /// let the grounding-honesty proof inject a known set and make the model claim chosen ids.
    /// </summary>
    public static (GenerationAgentDeps Deps, CountingChatClient Chat) EvalDeps(
        IEnumerable<string>? failTools = null,
        IEnumerable<string>? flakyTools = null,
        decimal globalCeilingUsd = 1.00m,
        IRetrievalService? retrieval = null,
        IEnumerable<string>? groundingClaim = null)
    {
        var chat = new CountingChatClient(new DeterministicGenerationChatClient(failTools, flakyTools, groundingClaim));
        var deps = new GenerationAgentDeps(
            Generator: new ForcedToolGenerator(chat),
            Retrieval: retrieval ?? new FakeRetrievalService(),
            Media: new DeterministicMediaGenerationTool(),
            Storage: new InMemoryStorageService(),
            Constraints: Constraints(),
            Prices: Prices(),
            GlobalCeilingUsd: globalCeilingUsd,
            SonnetModel: SonnetModel,
            HaikuModel: HaikuModel,
            Trace: new LocalTraceRecorder(),
            LoggerFactory: NullLoggerFactory.Instance,
            VeoStore: new VeoOperationStore());
        return (deps, chat);
    }

    /// <summary>Per-node retry counts from the call-counting chat client (retries = attempts - 1).</summary>
    public static IReadOnlyDictionary<string, int> OffStateRetries(CountingChatClient chat) =>
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [SystemOutput.Nodes.ContentStrategist] = chat.RetriesForTool("record_strategy_candidates"),
            [SystemOutput.Nodes.SupervisorSelection] = chat.RetriesForTool("record_selection"),
            [SystemOutput.Nodes.CreativeDirector] = chat.RetriesForTool("record_creative_direction"),
            [SystemOutput.Nodes.Copywriting] = chat.RetriesForTool("record_caption"),
        };

    /// <summary>
    /// Builds the real <see cref="MafOrchestrator"/>. Generation-only tests omit the publish deps
    /// (RunGenerationAsync never touches them); publish tests pass a real coordinator + brand-scoped
    /// db/scope so RunPublishAsync runs end-to-end.
    /// </summary>
    public static MafOrchestrator Orchestrator(
        GenerationAgentDeps deps,
        PublishCoordinator? coordinator = null,
        AppDbContext? db = null,
        IBrandScope? scope = null,
        ISecretsProvider? secrets = null,
        string publicBaseUrl = "")
        => new(
            deps,
            coordinator!,
            db!,
            scope!,
            secrets ?? new PassthroughSecretsProvider(),
            Microsoft.Extensions.Options.Options.Create(
                new Backend.Infrastructure.Configuration.Options.StorageOptions { PublicBaseUrl = publicBaseUrl }));

    public static CostPrices Prices() => new(
        SonnetInputPerMTok: 3m,
        SonnetOutputPerMTok: 15m,
        HaikuInputPerMTok: 1m,
        HaikuOutputPerMTok: 5m,
        GeminiPerImage: 0.04m);

    public static PlatformConstraintSet Constraints() => new(
    [
        new SurfaceConstraints("instagram_feed", MaxHashtags: 30, MaxCaptionLength: 2200, AllowedAspectRatios: ["4:5", "1:1"]),
        new SurfaceConstraints("instagram_reel", MaxHashtags: 30, MaxCaptionLength: 2200, AllowedAspectRatios: ["9:16"]),
        new SurfaceConstraints("instagram_story", MaxHashtags: 30, MaxCaptionLength: 2200, AllowedAspectRatios: ["9:16"]),
    ]);

    public static RunState Seed(
        Guid runId,
        Guid brandId,
        IReadOnlyList<string>? pillars = null,
        string surface = "instagram_feed",
        Budget? budget = null,
        string modality = "image",
        VideoSource videoSource = VideoSource.ImageSeed) =>
        new(
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
            Budget: budget ?? new Budget(TokenBudget: 10_000, TokensSpent: 0, MediaBudget: 1.00m, MediaSpent: 0m),
            Errors: [],
            Trace: new TraceRefs(string.Empty, [], []),
            TargetSurface: surface,
            ContentPillars: pillars ?? ["Origin", "Craft", "Ritual"],
            Candidates: null,
            IncurredCosts: [],
            FatalError: null,
            Modality: modality,
            VideoSource: videoSource);

    /// <summary>
    /// Seeds a video run (DL-058): <c>instagram_reel</c> surface (9:16) + the chosen source, plus a media
    /// budget large enough to cover one Veo clip by default. Pass a tiny <paramref name="budget"/> to force
    /// the pre-Media video-budget breach.
    /// </summary>
    public static RunState VideoSeed(
        Guid runId,
        Guid brandId,
        VideoSource videoSource = VideoSource.ImageSeed,
        Budget? budget = null) =>
        Seed(
            runId,
            brandId,
            surface: "instagram_reel",
            budget: budget ?? new Budget(TokenBudget: 10_000, TokensSpent: 0, MediaBudget: 5.00m, MediaSpent: 0m),
            modality: "video",
            videoSource: videoSource);
}
