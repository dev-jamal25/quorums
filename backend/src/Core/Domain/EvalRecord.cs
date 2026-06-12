namespace Backend.Core.Domain;

/// <summary>A consolidated evaluation metric for a run (wired up fully in Phase 9).</summary>
public sealed class EvalRecord : IBrandScoped
{
    public Guid Id { get; set; }

    public Guid BrandId { get; set; }

    public Guid? AgentRunId { get; set; }

    public string Metric { get; set; } = default!;

    public double Score { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
