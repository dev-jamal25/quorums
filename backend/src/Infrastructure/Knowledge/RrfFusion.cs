namespace Backend.Infrastructure.Knowledge;

/// <summary>
/// Reciprocal Rank Fusion (DL-025): the sanctioned way to fuse the dense and sparse recall arms
/// when no reranker is present (rerank OFF). It is rank-based — <c>score(id) = Σ_lists 1/(k + rank)</c>
/// (rank 1-based) — so it sidesteps the incomparable-scale problem of blending cosine vs ts_rank
/// directly. When the reranker is ON, the cross-encoder is the fusion authority and this is unused.
/// </summary>
internal static class RrfFusion
{
    /// <summary>The skill's named RRF constant (k ≈ 60).</summary>
    public const double DefaultK = 60.0;

    public static IReadOnlyDictionary<Guid, double> Fuse(
        IReadOnlyList<IReadOnlyList<Guid>> rankedLists, double k)
    {
        var scores = new Dictionary<Guid, double>();
        foreach (var list in rankedLists)
        {
            for (var rank = 0; rank < list.Count; rank++)
            {
                scores[list[rank]] = scores.GetValueOrDefault(list[rank], 0.0) + (1.0 / (k + rank + 1));
            }
        }

        return scores;
    }
}
