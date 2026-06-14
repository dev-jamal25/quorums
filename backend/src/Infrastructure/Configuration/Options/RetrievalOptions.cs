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
}
