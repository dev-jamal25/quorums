using System.Text.Json;

namespace Backend.Infrastructure.Evaluation.Judges;

/// <summary>One locked calibration item: the generated <see cref="Output"/> plus the human gold labels on
/// the two κ-gated axes (DL-057). <see cref="Tag"/> marks the adversarial items (JC-13…16).</summary>
public sealed record CalibrationCase(string Id, string? Tag, string Query, string Output, bool BrandOn, bool Grounded);

/// <summary>The loaded calibration set + its version (for the EvalRun provenance).</summary>
public sealed record JudgeCalibrationSet(string Version, int N, IReadOnlyList<CalibrationCase> Cases);

/// <summary>
/// Dedicated reader for the locked <c>judge-calibration.json</c> (DL-057). The file's per-case shape fits
/// <see cref="Backend.Core.Evaluation.EvalCase"/> (raw-JSON input/expected) but its judge-specific
/// <c>expected:{brand,grounded}</c> labels and its <c>_meta:{dataset,n}</c> block warrant a purpose-built
/// reader rather than overloading the generic <c>JsonDatasetLoader</c> (which validates <c>_meta.name</c>/
/// <c>size</c>) or rewriting the locked file. No schema change to EvalCase; the file is read as-is.
/// </summary>
public static class JudgeCalibrationDataset
{
    public static async Task<JudgeCalibrationSet> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"judge calibration set not found at '{path}'.", path);
        }

        await using var stream = File.OpenRead(path);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;

        var meta = root.GetProperty("_meta");
        var version = meta.TryGetProperty("version", out var v) ? v.GetString() ?? "v0" : "v0";
        var declaredN = meta.TryGetProperty("n", out var nEl) && nEl.ValueKind == JsonValueKind.Number ? nEl.GetInt32() : -1;

        var cases = new List<CalibrationCase>();
        foreach (var element in root.GetProperty("cases").EnumerateArray())
        {
            var id = element.GetProperty("id").GetString()
                ?? throw new InvalidOperationException($"calibration set '{path}' has a case with no id.");
            var tag = element.TryGetProperty("tag", out var t) ? t.GetString() : null;
            var input = element.GetProperty("input");
            var expected = element.GetProperty("expected");

            cases.Add(new CalibrationCase(
                Id: id,
                Tag: tag,
                Query: input.GetProperty("query").GetString() ?? string.Empty,
                Output: input.GetProperty("output").GetString() ?? string.Empty,
                BrandOn: ReadLabel(expected, "brand", "on", "off", path, id),
                Grounded: ReadLabel(expected, "grounded", "yes", "no", path, id)));
        }

        if (cases.Count == 0)
        {
            throw new InvalidOperationException($"calibration set '{path}' has no cases.");
        }

        if (cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != cases.Count)
        {
            throw new InvalidOperationException($"calibration set '{path}' has duplicate case ids.");
        }

        if (declaredN >= 0 && declaredN != cases.Count)
        {
            throw new InvalidOperationException(
                $"calibration set '{path}' _meta.n ({declaredN}) does not match the case count ({cases.Count}).");
        }

        return new JudgeCalibrationSet(version, cases.Count, cases);
    }

    private static bool ReadLabel(JsonElement expected, string property, string trueValue, string falseValue, string path, string id)
    {
        var raw = expected.TryGetProperty(property, out var value) ? value.GetString() : null;
        if (string.Equals(raw, trueValue, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(raw, falseValue, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        throw new InvalidOperationException(
            $"calibration set '{path}' case '{id}' has invalid {property} label '{raw}' (expected '{trueValue}' or '{falseValue}').");
    }
}
