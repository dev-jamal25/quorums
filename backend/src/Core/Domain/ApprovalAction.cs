namespace Backend.Core.Domain;

/// <summary>Audit record of a human approve/reject decision on a run (DL-005, DL-021).</summary>
public sealed class ApprovalAction : IBrandScoped
{
    public Guid Id { get; set; }

    public Guid BrandId { get; set; }

    public Guid AgentRunId { get; set; }

    public ApprovalDecision Decision { get; set; }

    public string DecidedBy { get; set; } = default!;

    public DateTimeOffset DecidedAt { get; set; }
}
