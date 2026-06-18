namespace Backend.Core.Domain;

/// <summary>
/// One persisted evaluation result (DL-051): a single case × evaluator outcome under a parent
/// <see cref="EvalRun"/>. Brand-scoped (rides the same RLS policy as <see cref="EvalRun"/>). The CLR
/// type is named <c>EvalResultRow</c> to read as a persistence row and to avoid clashing with the
/// library's <c>Microsoft.Extensions.AI.Evaluation.EvaluationResult</c>; the table is <c>eval_results</c>
/// (DbSet-name convention, like <c>eval_records</c>).
/// </summary>
public sealed class EvalResultRow : IBrandScoped
{
    public Guid Id { get; set; }

    /// <summary>FK to <see cref="EvalRun.Id"/>.</summary>
    public Guid RunId { get; set; }

    public Guid BrandId { get; set; }

    public string CaseId { get; set; } = default!;

    public string EvaluatorName { get; set; } = default!;

    public double Score { get; set; }

    public string? Reasoning { get; set; }

    public decimal? CostUsd { get; set; }

    public long? LatencyMs { get; set; }

    /// <summary>Structured detail (judge bucket, ablation toggles, failing field, …). Serialized to jsonb.</summary>
    public IReadOnlyDictionary<string, object>? Metadata { get; set; }
}
