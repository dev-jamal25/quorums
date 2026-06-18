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

    /// <summary>
    /// Eval variant: deterministic deps wired with the read-only recording doubles (Option A) so a test
    /// can run a mock-mode generation and then read the two off-state fields — injected provenance ids
    /// (from the recording retrieval) and per-node retry counts (from the call-counting chat client) —
    /// that are not recoverable from RunState/trace. No production code or frozen contract changes.
    /// </summary>
    public static (GenerationAgentDeps Deps, RecordingRetrievalService Retrieval, CountingChatClient Chat) EvalDeps(
        IEnumerable<string>? failTools = null,
        IEnumerable<string>? flakyTools = null,
        decimal globalCeilingUsd = 1.00m)
    {
        var chat = new CountingChatClient(new DeterministicGenerationChatClient(failTools, flakyTools));
        var retrieval = new RecordingRetrievalService(new FakeRetrievalService());
        var deps = new GenerationAgentDeps(
            Generator: new ForcedToolGenerator(chat),
            Retrieval: retrieval,
            Media: new DeterministicMediaGenerationTool(),
            Storage: new InMemoryStorageService(),
            Constraints: Constraints(),
            Prices: Prices(),
            GlobalCeilingUsd: globalCeilingUsd,
            SonnetModel: SonnetModel,
            HaikuModel: HaikuModel,
            Trace: new LocalTraceRecorder(),
            LoggerFactory: NullLoggerFactory.Instance);
        return (deps, retrieval, chat);
    }

    /// <summary>Projects the two off-state fields from the recording doubles, keyed by graph node.</summary>
    public static (IReadOnlyDictionary<string, IReadOnlyList<string>> Injected, IReadOnlyDictionary<string, int> Retries) OffState(
        RecordingRetrievalService retrieval, CountingChatClient chat)
    {
        var injected = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [SystemOutput.Nodes.ContentStrategist] = retrieval.AllProvenanceIds,
            [SystemOutput.Nodes.CreativeDirector] = retrieval.AllProvenanceIds,
            [SystemOutput.Nodes.Copywriting] = retrieval.AllProvenanceIds,
        };

        var retries = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [SystemOutput.Nodes.ContentStrategist] = chat.RetriesForTool("record_strategy_candidates"),
            [SystemOutput.Nodes.SupervisorSelection] = chat.RetriesForTool("record_selection"),
            [SystemOutput.Nodes.CreativeDirector] = chat.RetriesForTool("record_creative_direction"),
            [SystemOutput.Nodes.Copywriting] = chat.RetriesForTool("record_caption"),
        };

        return (injected, retries);
    }

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
        ISecretsProvider? secrets = null)
        => new(deps, coordinator!, db!, scope!, secrets ?? new PassthroughSecretsProvider());

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
