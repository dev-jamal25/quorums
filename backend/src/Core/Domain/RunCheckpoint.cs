namespace Backend.Core.Domain;

/// <summary>
/// Durable graph state for an <see cref="AgentRun"/>, written by the worker so a run
/// can pause at the human gate and resume on a later job (DL-006). State passes
/// through Postgres, never through job payloads.
/// </summary>
public sealed class RunCheckpoint : IBrandScoped
{
    public Guid Id { get; set; }

    public Guid BrandId { get; set; }

    public Guid AgentRunId { get; set; }

    public string StateJson { get; set; } = default!;

    public DateTimeOffset CreatedAt { get; set; }
}
