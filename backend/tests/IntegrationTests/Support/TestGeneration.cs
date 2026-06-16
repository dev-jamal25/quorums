using Backend.Core.Generation.Cost;
using Backend.Core.Generation.PlatformConstraints;
using Backend.Core.Integrations;
using Backend.Core.Knowledge;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Backend.Core.Storage;
using Backend.Infrastructure.Generation;
using Backend.Infrastructure.Orchestration.Maf;
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
        ITrace? trace = null)
    {
        var chat = new DeterministicGenerationChatClient(failTools, flakyTools);
        return new GenerationAgentDeps(
            Generator: new ForcedToolGenerator(chat),
            Retrieval: retrieval ?? new FakeRetrievalService(),
            Media: media ?? new Backend.Infrastructure.Integrations.Gemini.DeterministicMediaGenerationTool(),
            Storage: storage ?? new InMemoryStorageService(),
            Constraints: Constraints(),
            Prices: Prices(),
            GlobalCeilingUsd: globalCeilingUsd,
            SonnetModel: SonnetModel,
            HaikuModel: HaikuModel,
            Trace: trace ?? new LocalTraceRecorder(),
            LoggerFactory: NullLoggerFactory.Instance);
    }

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
        Budget? budget = null) =>
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
            FatalError: null);
}
