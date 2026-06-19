namespace Backend.Core.Domain;

/// <summary>One per-metric aggregate over an eval run: the mean score and the n it was taken over.</summary>
public sealed record MetricAggregate(double Mean, int N);

/// <summary>
/// One persisted evaluation run (DL-051, deck S16) — the provenance needed to reproduce a result.
/// Brand-scoped like every tenant-owned table: the RLS policy rides this table's creating migration,
/// and writes go through the transaction-scoped <c>app.current_brand</c> bind (never a manual
/// <c>WHERE brand_id</c>). <see cref="PromptVersion"/> is a constant placeholder until generation
/// prompts are versioned (a recorded follow-up, not built here).
/// </summary>
public sealed class EvalRun : IBrandScoped
{
    public Guid Id { get; set; }

    public Guid BrandId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string GitSha { get; set; } = default!;

    public string PromptVersion { get; set; } = default!;

    public string ModelName { get; set; } = default!;

    public string ModelVersion { get; set; } = default!;

    public double Temperature { get; set; }

    public string DatasetName { get; set; } = default!;

    public string DatasetVersion { get; set; } = default!;

    public int DatasetSize { get; set; }

    /// <summary>Aggregate metrics keyed by evaluator/metric name (mean + n). Serialized to a jsonb column.</summary>
    public IReadOnlyDictionary<string, MetricAggregate> Aggregates { get; set; } =
        new Dictionary<string, MetricAggregate>();
}
