using Backend.Core.Evaluation;

namespace Backend.Infrastructure.Evaluation;

/// <summary>The <c>_meta</c> block of a dataset file (DL-047): identity, version, size, criteria.</summary>
public sealed record EvalDatasetMeta(
    string Name,
    string Version,
    string? CreatedAt,
    int Size,
    string? Criteria);

/// <summary>A loaded, validated dataset: its <c>_meta</c> plus the labelled cases.</summary>
public sealed record EvalDataset(EvalDatasetMeta Meta, IReadOnlyList<EvalCase> Cases);
