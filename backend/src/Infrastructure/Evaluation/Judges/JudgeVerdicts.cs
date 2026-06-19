using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backend.Infrastructure.Evaluation.Judges;

/// <summary>One item's committed judge verdict + the human gold label, on both κ-gated axes.</summary>
public sealed record JudgeVerdictItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("tag")] string? Tag,
    [property: JsonPropertyName("judge_brand")] bool JudgeBrand,
    [property: JsonPropertyName("judge_grounded")] bool JudgeGrounded,
    [property: JsonPropertyName("human_brand")] bool HumanBrand,
    [property: JsonPropertyName("human_grounded")] bool HumanGrounded);

/// <summary>
/// The durable, committed record of one calibration run's LLM-judge verdicts (DL-057) — the cached judge
/// outputs. The live calibration writes it (the one-time spend); the no-spend CI replay reads it back and
/// recomputes Cohen's κ + the adversarial asserts deterministically, with zero Gemini calls. Regenerated
/// (re-spent) only when the judge prompt changes.
/// </summary>
public sealed record JudgeVerdictSet(
    [property: JsonPropertyName("git_sha")] string GitSha,
    [property: JsonPropertyName("threshold")] int Threshold,
    [property: JsonPropertyName("kappa_brand")] double KappaBrand,
    [property: JsonPropertyName("kappa_grounded")] double KappaGrounded,
    [property: JsonPropertyName("items")] IReadOnlyList<JudgeVerdictItem> Items);

public static class JudgeVerdicts
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static void Write(string path, JudgeCalibrationResult result, int threshold, string gitSha)
    {
        ArgumentNullException.ThrowIfNull(result);

        var set = new JudgeVerdictSet(
            gitSha,
            threshold,
            result.KappaBrand,
            result.KappaGrounded,
            result.Items
                .Select(i => new JudgeVerdictItem(i.Id, i.Tag, i.JudgeBrand, i.JudgeGrounded, i.HumanBrand, i.HumanGrounded))
                .ToList());

        File.WriteAllText(path, JsonSerializer.Serialize(set, _json));
    }

    public static async Task<JudgeVerdictSet> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"committed judge verdicts not found at '{path}'.", path);
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<JudgeVerdictSet>(stream, _json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"judge verdicts '{path}' deserialized to null.");
    }
}
