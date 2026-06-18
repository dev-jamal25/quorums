using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Backend.Core.Evaluation;

namespace Backend.Infrastructure.Evaluation;

/// <summary>
/// Loads a versioned JSON dataset from <c>eval/datasets/&lt;brandId&gt;/&lt;name&gt;.json</c> and
/// validates its <c>_meta</c> block + the deck's bump-rule field (<c>size</c> must equal the case
/// count). A malformed dataset throws a clear exception — never a silent partial load (DL-022).
/// </summary>
public static partial class JsonDatasetLoader
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<EvalDataset> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"eval dataset not found at '{path}'.", path);
        }

        await using var stream = File.OpenRead(path);
        var raw = await JsonSerializer.DeserializeAsync<RawDataset>(stream, _json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"eval dataset '{path}' deserialized to null.");

        Validate(path, raw);

        var cases = raw.Cases!
            .Select(c => new EvalCase(
                c.Id!,
                c.Input,
                c.Expected,
                c.Tags ?? [],
                c.Notes,
                c.AddedBy,
                c.AddedAt))
            .ToList();

        var meta = raw.Meta!;
        return new EvalDataset(
            new EvalDatasetMeta(meta.Name!, meta.Version!, meta.CreatedAt, meta.Size, meta.Criteria),
            cases);
    }

    private static void Validate(string path, RawDataset raw)
    {
        if (raw.Meta is null)
        {
            throw new InvalidOperationException($"eval dataset '{path}' is missing the required '_meta' block.");
        }

        if (string.IsNullOrWhiteSpace(raw.Meta.Name))
        {
            throw new InvalidOperationException($"eval dataset '{path}' _meta.name is required.");
        }

        if (string.IsNullOrWhiteSpace(raw.Meta.Version) || !SemVerRegex().IsMatch(raw.Meta.Version))
        {
            throw new InvalidOperationException(
                $"eval dataset '{path}' _meta.version '{raw.Meta.Version}' must be semver (major.minor.patch).");
        }

        var cases = raw.Cases ?? [];
        if (cases.Any(c => string.IsNullOrWhiteSpace(c.Id)))
        {
            throw new InvalidOperationException($"eval dataset '{path}' has a case with no id.");
        }

        var duplicate = cases.GroupBy(c => c.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"eval dataset '{path}' has a duplicate case id '{duplicate.Key}'.");
        }

        // The bump rule: _meta.size must equal the actual case count (add a case → patch + size bump).
        if (raw.Meta.Size != cases.Count)
        {
            throw new InvalidOperationException(
                $"eval dataset '{path}' _meta.size ({raw.Meta.Size}) does not match the case count ({cases.Count}).");
        }
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex SemVerRegex();

    private sealed record RawDataset(
        [property: JsonPropertyName("_meta")] RawMeta? Meta,
        List<RawCase>? Cases);

    private sealed record RawMeta(string? Name, string? Version, string? CreatedAt, int Size, string? Criteria);

    private sealed record RawCase(
        string? Id,
        JsonElement Input,
        JsonElement Expected,
        List<string>? Tags,
        string? Notes,
        string? AddedBy,
        string? AddedAt);
}
