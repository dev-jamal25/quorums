using Backend.Core.Knowledge;

namespace Backend.Infrastructure.Knowledge;

/// <summary>
/// Offline deterministic multi-query expander for CI (DL-025). Produces stable paraphrase-like
/// variants so the S0 ablation runs without calling Claude.
/// </summary>
public sealed class DeterministicQueryTransformer : IQueryTransformer
{
    private static readonly string[] _lenses = ["", " overview", " details", " examples", " guidance"];

    public Task<IReadOnlyList<string>> ExpandAsync(
        string query, int variants, CancellationToken cancellationToken = default)
    {
        var n = Math.Max(1, variants);
        var list = Enumerable.Range(0, n).Select(i => (query + _lenses[i % _lenses.Length]).Trim()).ToList();
        return Task.FromResult<IReadOnlyList<string>>(list);
    }
}
