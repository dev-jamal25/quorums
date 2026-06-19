namespace Backend.Core.Evaluation;

/// <summary>
/// Deterministic reference-based retrieval rank metrics (evaluators.md §2, DL-048). Pure functions over a
/// ranked chunk-id list and the golden relevant set R_q — no LLM, no I/O. Chosen to measure *ordering*
/// (what the hybrid + reranker change) on a small per-tenant corpus, so they do not saturate the way
/// recall@large-k does. The custom <c>IEvaluator</c>s in Infrastructure delegate here; the harness then
/// aggregates the per-query values (the mean of <see cref="ReciprocalRank"/> over queries IS MRR).
/// <c>R_q</c> is expected non-empty (the golden labels guarantee it); a vacuous/empty R_q yields 0.
/// </summary>
public static class RankMetrics
{
    /// <summary>Context recall @ k — <c>|{d_1..d_k} ∩ R_q| / |R_q|</c>. Report at small k only (k = 1, 3).</summary>
    public static double Recall(IReadOnlyList<Guid> ranked, IReadOnlyCollection<Guid> relevant, int k)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(relevant);

        var rel = AsSet(relevant);
        if (rel.Count == 0 || k <= 0)
        {
            return 0.0;
        }

        var hits = TopK(ranked, k).Count(rel.Contains);
        return hits / (double)rel.Count;
    }

    /// <summary>Hit@1 — 1 if the top-ranked chunk is relevant, else 0.</summary>
    public static double HitAtOne(IReadOnlyList<Guid> ranked, IReadOnlyCollection<Guid> relevant)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(relevant);

        return ranked.Count > 0 && AsSet(relevant).Contains(ranked[0]) ? 1.0 : 0.0;
    }

    /// <summary>
    /// Reciprocal rank — <c>1 / (1-based rank of the first relevant chunk)</c>; 0 if none is retrieved.
    /// The mean of this over the query set is MRR.
    /// </summary>
    public static double ReciprocalRank(IReadOnlyList<Guid> ranked, IReadOnlyCollection<Guid> relevant)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(relevant);

        var rel = AsSet(relevant);
        for (var i = 0; i < ranked.Count; i++)
        {
            if (rel.Contains(ranked[i]))
            {
                return 1.0 / (i + 1);
            }
        }

        return 0.0;
    }

    /// <summary>
    /// Rank-aware context precision over the top-k (average precision @ k): for each rank <c>i</c> where
    /// <c>d_i</c> is relevant, take precision@i = <c>(relevant in d_1..d_i) / i</c>, then average those over
    /// <c>|R_q ∩ top-k|</c>. Rewards ranking relevant chunks higher — the primary stage-discriminating
    /// metric. Returns 0 when no relevant chunk is retrieved in the top-k.
    /// </summary>
    public static double ContextPrecision(IReadOnlyList<Guid> ranked, IReadOnlyCollection<Guid> relevant, int k)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(relevant);

        var rel = AsSet(relevant);
        var topK = TopK(ranked, k);

        var relevantSoFar = 0;
        var sum = 0.0;
        for (var i = 0; i < topK.Count; i++)
        {
            if (rel.Contains(topK[i]))
            {
                relevantSoFar++;
                sum += relevantSoFar / (double)(i + 1);
            }
        }

        return relevantSoFar == 0 ? 0.0 : sum / relevantSoFar;
    }

    private static IReadOnlyList<Guid> TopK(IReadOnlyList<Guid> ranked, int k) =>
        k >= ranked.Count ? ranked : ranked.Take(k).ToList();

    private static HashSet<Guid> AsSet(IReadOnlyCollection<Guid> relevant) =>
        relevant as HashSet<Guid> ?? [.. relevant];
}
