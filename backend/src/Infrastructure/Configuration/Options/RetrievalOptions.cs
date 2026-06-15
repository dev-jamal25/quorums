namespace Backend.Infrastructure.Configuration.Options;

/// <summary>
/// The config-gated retrieval stage seam (DL-025). Slice 2 wires only the dense arm; the
/// other stages are present-but-off so slice 3 fills them by flipping a flag — never a
/// literal in code (the Phase-9 ablation precondition).
/// </summary>
public sealed class RetrievalOptions
{
    public const string SectionName = "Retrieval";

    /// <summary>S0 query transform (slice 3). Off → the pipeline runs on the single query.</summary>
    public bool QueryTransformEnabled { get; init; }

    public int QueryVariants { get; init; } = 3;

    /// <summary>S1 dense recall arm. The only wired arm in slice 2.</summary>
    public bool DenseEnabled { get; init; } = true;

    /// <summary>S1 sparse FTS recall arm (slice 3).</summary>
    public bool SparseEnabled { get; init; }

    /// <summary>S1 per-arm recall depth (N).</summary>
    public int RecallDepth { get; init; } = 20;

    /// <summary>S2 cross-encoder rerank (slice 3).</summary>
    public bool RerankEnabled { get; init; }

    /// <summary>S2 final cut (k).</summary>
    public int FinalK { get; init; } = 5;

    /// <summary>S2 metadata blend weights (DL-025). Defaults: α=1 relevance; β/δ boost; γ inert
    /// (no target segment until agent wiring). With β=γ=δ=0 the blend collapses to pure rerank order.</summary>
    public RetrievalBlendOptions Blend { get; init; } = new();
}

/// <summary>
/// The S2 metadata-blend weights (DL-025, JC-2). The reranker stays pure; these weights are the
/// only place metadata (performance / recency / segment) touches the final score. Config-bound,
/// never literals. With β=γ=δ=0 the blend collapses to the pure reranker order.
/// </summary>
public sealed class RetrievalBlendOptions
{
    /// <summary>α — relevance weight (baseline = 1).</summary>
    public double Alpha { get; init; } = 1.0;

    /// <summary>β — historical_post normalized performance ((engagement_rate + ctr) / 2).</summary>
    public double Beta { get; init; } = 0.3;

    /// <summary>γ — historical_post audience-segment match. Inert in slice 3 (no target segment yet).</summary>
    public double Gamma { get; init; }

    /// <summary>δ — market_intel recency decay.</summary>
    public double Delta { get; init; } = 0.3;

    /// <summary>Half-life (days) for the market_intel recency decay 2^(-ageDays / halfLife).</summary>
    public double RecencyHalfLifeDays { get; init; } = 30.0;
}
