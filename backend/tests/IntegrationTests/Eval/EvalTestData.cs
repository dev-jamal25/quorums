using System.Text.Json;
using Backend.Core.Evaluation;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Microsoft.Extensions.AI;

namespace Backend.IntegrationTests.Eval;

/// <summary>
/// Builders for the rule-based evaluator unit tests: a fully-valid <see cref="SystemOutput"/> (every §1
/// evaluator passes) plus the empty conversation the library's <c>EvaluateAsync</c> requires. Adversarial
/// cases are produced by the tests via <c>with</c> mutations.
/// </summary>
internal static class EvalTestData
{
    public static readonly IReadOnlyList<string> Pillars = ["Origin", "Craft", "Ritual"];

    public const string Surface = "instagram_feed";

    public static EvalCase Case(string id = "TC-unit") =>
        new(id, EmptyJson(), EmptyJson(), [], null, "test", "2026-06-19");

    public static (List<ChatMessage> Messages, ChatResponse Response) Conversation()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "generate") };
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hook\n\nBody"));
        return (messages, response);
    }

    /// <summary>A complete, on-brand run that satisfies every §1 rule-based evaluator.</summary>
    public static SystemOutput ValidOutput()
    {
        var grounding = new Grounding(Grounded: false, ChunkIdsUsed: [], Confidence.Low);

        var candidates = Enumerable.Range(0, 3)
            .Select(i => new ContentStrategy(
                Pillar: "Origin",
                Angle: $"angle {i}",
                Objective: Objective.Awareness,
                Audience: "home brewers",
                AngleRationale: $"rationale {i}",
                CalendarSlot: null,
                Grounding: grounding))
            .ToList();

        var brief = new MediaPromptBrief(
            Subject: "a pour-over cup",
            Style: "warm editorial",
            Composition: "centered",
            Palette: "earthy",
            Mood: "calm",
            Negative: null,
            AspectRatio: "4:5");

        var creative = new CreativeDirection(
            VisualConcept: "steam over a mug",
            StyleTokens: ["warm", "natural-light"],
            ColorTokens: [new ColorToken("kraft", "#C9A27E")],
            MediaPromptBrief: brief,
            Grounding: grounding);

        var caption = new Caption(
            Hook: "Slow mornings start here",
            Body: "A pour-over ritual with our Ethiopia Yirgacheffe.",
            Hashtags: ["#coffee", "#pourover"],
            Grounding: grounding);

        var brandId = Guid.NewGuid();
        var media = new MediaAssetRef(Guid.NewGuid(), $"brands/{brandId}/assets/x.png", "image", "image/png");
        var draft = new ContentItemDraft(caption, media, brandId, "Assembled");

        return new SystemOutput(
            RunId: Guid.NewGuid(),
            BrandId: brandId,
            TargetSurface: Surface,
            ContentPillars: Pillars,
            Candidates: new StrategyCandidates(candidates),
            Strategy: candidates[0],
            ChosenIndex: 0,
            Creative: creative,
            Caption: caption,
            Media: media,
            Draft: draft,
            BudgetDegraded: false,
            GeminiCallCount: 1,
            Budget: new Budget(TokenBudget: 10_000, TokensSpent: 100, MediaBudget: 1.00m, MediaSpent: 0.04m),
            Errors: [],
            FatalError: null,
            InjectedChunkIdsByNode: new Dictionary<string, IReadOnlyList<string>>(),
            RetryCountsByNode: new Dictionary<string, int>(),
            Trace: new TraceRefs(string.Empty, [], []));
    }

    private static JsonElement EmptyJson() => JsonSerializer.SerializeToElement(new { });
}
