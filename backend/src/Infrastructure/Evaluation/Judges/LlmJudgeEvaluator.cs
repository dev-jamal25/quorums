using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace Backend.Infrastructure.Evaluation.Judges;

/// <summary>
/// Base for the Phase-9 LLM-judge tier (DL-057): a custom <see cref="IEvaluator"/> that prompts the judge
/// <see cref="IChatClient"/> (Gemini, supplied via <see cref="ChatConfiguration"/> so the framework's
/// response cache wraps it — real once, cached replay thereafter at zero spend), parses a structured JSON
/// verdict, and binarizes it at the config-bound pass threshold into a <see cref="BooleanMetric"/>. A
/// missing client, a transport failure, or an unparseable verdict <b>reds</b> the metric (a judge that
/// cannot answer is not a pass). Subclasses own the rubric prompt + the score→pass rule.
/// </summary>
public abstract class LlmJudgeEvaluator : IEvaluator
{
    private static readonly JsonSerializerOptions _json =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    protected LlmJudgeEvaluator(int passThreshold) => PassThreshold = passThreshold;

    /// <summary>The 1–5 score a dimension must reach to pass (config-bound, never a literal).</summary>
    protected int PassThreshold { get; }

    protected abstract string MetricName { get; }

    public IReadOnlyCollection<string> EvaluationMetricNames => [MetricName];

    public async ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        var metric = new BooleanMetric(MetricName);

        if (chatConfiguration?.ChatClient is not { } judge)
        {
            return Red(metric, "no judge ChatClient was supplied (ChatConfiguration is required for the LLM judge)");
        }

        if (additionalContext?.OfType<JudgeContext>().FirstOrDefault() is not { } context)
        {
            return Red(metric, $"a value of type {nameof(JudgeContext)} was not found in additionalContext");
        }

        ChatResponse verdict;
        try
        {
            verdict = await judge.GetResponseAsync(
                [new ChatMessage(ChatRole.System, SystemPrompt), new ChatMessage(ChatRole.User, BuildPrompt(context))],
                new ChatOptions { Temperature = 0f },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Red(metric, $"judge call failed: {ex.Message}");
        }

        if (ExtractJson(verdict.Text) is not { } json)
        {
            return Red(metric, $"judge returned an unparseable verdict: {Trim(verdict.Text)}");
        }

        var (passed, reason, dimensions) = Score(json);
        metric.Value = passed;
        metric.Reason = reason;
        metric.Interpretation = new EvaluationMetricInterpretation(failed: !passed, reason: reason);
        foreach (var (name, score) in dimensions)
        {
            metric.AddDiagnostics(EvaluationDiagnostic.Informational($"{name}={score}"));
        }

        return new EvaluationResult(metric);
    }

    /// <summary>The judge's system role / rubric framing.</summary>
    protected abstract string SystemPrompt { get; }

    /// <summary>Builds the per-item user prompt (cites the standards/grounding from the context).</summary>
    protected abstract string BuildPrompt(JudgeContext context);

    /// <summary>Reads the verdict JSON → (pass/fail, reasoning, per-dimension scores).</summary>
    protected abstract (bool Passed, string Reason, IReadOnlyList<(string Name, int Score)> Dimensions) Score(JsonElement verdict);

    protected static int ReadScore(JsonElement verdict, string property) =>
        verdict.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    protected static string ReadReason(JsonElement verdict) =>
        verdict.TryGetProperty("reasoning", out var value) && value.GetString() is { } reason
            ? reason
            : "(no reasoning provided)";

    private static EvaluationResult Red(BooleanMetric metric, string reason)
    {
        metric.Value = false;
        metric.Reason = reason;
        metric.Interpretation = new EvaluationMetricInterpretation(failed: true, reason: reason);
        metric.AddDiagnostics(EvaluationDiagnostic.Error(reason));
        return new EvaluationResult(metric);
    }

    // Models sometimes wrap JSON in prose / markdown fences; take the first balanced {…} object.
    private static JsonElement? ExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(text[start..(end + 1)], _json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Trim(string text) => text.Length <= 200 ? text : text[..200] + "…";
}
