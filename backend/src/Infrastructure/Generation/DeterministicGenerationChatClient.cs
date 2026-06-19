using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Backend.Core.Orchestration.Contracts;
using Microsoft.Extensions.AI;

namespace Backend.Infrastructure.Generation;

/// <summary>
/// Deterministic, network-free generation <see cref="IChatClient"/> (the slice-2/3 CI-mock pattern,
/// selected by a chat mode of <c>mock</c>): it reads the forced tool name and returns a canned,
/// schema-valid <c>tool_use</c> per agent — so CI/compose run the real agents with zero live Claude
/// calls (CLAUDE.md: CI on mocks only). The Strategist's pillar is parsed from the prompt's allowed
/// list, so the canned output validates against any brand. Tools named in <c>failTools</c> instead
/// return output that fails validation/schema on every attempt, exercising the bounded-retry → fatal
/// path; <c>flakyTools</c> fail once then succeed (invalid→valid).
/// </summary>
public sealed partial class DeterministicGenerationChatClient : IChatClient
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HashSet<string> _failTools;
    private readonly HashSet<string> _flakyTools;
    private readonly IReadOnlyList<string>? _groundingClaim;
    private readonly Dictionary<string, int> _callCounts = new(StringComparer.Ordinal);

    public DeterministicGenerationChatClient(
        IEnumerable<string>? failTools = null,
        IEnumerable<string>? flakyTools = null,
        IEnumerable<string>? groundingClaim = null)
    {
        _failTools = (failTools ?? []).ToHashSet(StringComparer.Ordinal);
        _flakyTools = (flakyTools ?? []).ToHashSet(StringComparer.Ordinal);
        // When set, the canned agent outputs CLAIM these chunk ids in their grounding (raw, pre-reconcile)
        // — used by the DL-054 grounding-honesty end-to-end proof. Default: ungrounded (empty claim).
        _groundingClaim = groundingClaim?.ToList();
    }

    private Grounding ClaimGrounding() =>
        new(Grounded: _groundingClaim is { Count: > 0 }, ChunkIdsUsed: _groundingClaim ?? [], Confidence.Medium);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var toolName = ResolveToolName(options);
        var prompt = string.Join("\n", messages.Select(message => message.Text));

        var attempt = _callCounts.TryGetValue(toolName, out var n) ? n : 0;
        _callCounts[toolName] = attempt + 1;

        // Fail every attempt; or (flaky) fail only the first attempt, then succeed.
        var invalid = _failTools.Contains(toolName) || (_flakyTools.Contains(toolName) && attempt == 0);

        var arguments = BuildArguments(toolName, prompt, invalid);
        var content = new FunctionCallContent("call-deterministic", toolName, arguments);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, new List<AIContent> { content })));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("the forced-tool seam is non-streaming.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        // Nothing to dispose.
    }

    private static string ResolveToolName(ChatOptions? options)
    {
        if (options?.ToolMode is RequiredChatToolMode { RequiredFunctionName: { } name })
        {
            return name;
        }

        return options?.Tools?.OfType<AITool>().FirstOrDefault()?.Name ?? string.Empty;
    }

    private Dictionary<string, object?> BuildArguments(string toolName, string prompt, bool invalid) =>
        toolName switch
        {
            "record_strategy_candidates" => StrategyArguments(prompt, invalid),
            "record_selection" => SelectionArguments(invalid),
            "record_creative_direction" => CreativeArguments(invalid),
            "record_caption" => CaptionArguments(invalid),
            _ => new Dictionary<string, object?>(),
        };

    private Dictionary<string, object?> StrategyArguments(string prompt, bool invalid)
    {
        if (invalid)
        {
            // schema violation: candidates is not an array (fails regardless of the brand's pillars).
            return new Dictionary<string, object?> { ["candidates"] = "not-an-array" };
        }

        var pillar = ExtractPillar(prompt);
        var grounding = ClaimGrounding();
        var candidates = Enumerable.Range(0, 3)
            .Select(i => new ContentStrategy(
                Pillar: pillar,
                Angle: $"distinct angle {i}",
                Objective: Objective.Awareness,
                Audience: "home brewers who value provenance and ritual",
                AngleRationale: $"this angle fits the brand because {i}",
                CalendarSlot: null,
                Grounding: grounding))
            .ToList();
        return ToArguments(new StrategyCandidates(candidates));
    }

    private static Dictionary<string, object?> SelectionArguments(bool invalid) =>
        // invalid → chosenIndex out of range (a validation failure).
        ToArguments(new SelectionDecision(ChosenIndex: invalid ? 99 : 0, Rationale: "the strongest on-brand angle"));

    private Dictionary<string, object?> CreativeArguments(bool invalid)
    {
        if (invalid)
        {
            // schema violation: styleTokens is not an array.
            return new Dictionary<string, object?> { ["visualConcept"] = "x", ["styleTokens"] = "not-an-array" };
        }

        var brief = new MediaPromptBrief(
            Subject: "a single pour-over cup with rising steam",
            Style: "warm editorial photography",
            Composition: "centered, shallow depth of field",
            Palette: "earthy kraft tones",
            Mood: "calm, slow morning",
            Negative: null,
            AspectRatio: "16:9"); // intentionally non-surface — the CD stamps the correct value (R8)
        var creative = new CreativeDirection(
            VisualConcept: "steam curling over a kraft-toned mug in natural light",
            StyleTokens: ["warm", "natural-light", "texture-over-gloss"],
            ColorTokens: [new ColorToken("kraft", "#C9A27E")],
            MediaPromptBrief: brief,
            Grounding: ClaimGrounding());
        return ToArguments(creative);
    }

    private Dictionary<string, object?> CaptionArguments(bool invalid)
    {
        if (invalid)
        {
            // schema violation: hashtags is not an array.
            return new Dictionary<string, object?> { ["hook"] = "h", ["body"] = "b", ["hashtags"] = "not-an-array" };
        }

        var caption = new Caption(
            Hook: "Slow mornings start here",
            Body: "A pour-over ritual with our Ethiopia Yirgacheffe — bloom, pour, breathe.",
            Hashtags: ["#coffee", "#pourover", "#singleorigin"],
            Grounding: ClaimGrounding());
        return ToArguments(caption);
    }

    private static string ExtractPillar(string prompt)
    {
        var match = PillarListRegex().Match(prompt);
        if (match.Success)
        {
            var first = match.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
            {
                return first;
            }
        }

        return "Origin";
    }

    private static Dictionary<string, object?> ToArguments<T>(T record)
    {
        var element = JsonSerializer.SerializeToElement(record, _json);
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(element, _json)!;
    }

    [GeneratedRegex(@"one of:\s*\[([^\]]*)\]")]
    private static partial Regex PillarListRegex();
}
