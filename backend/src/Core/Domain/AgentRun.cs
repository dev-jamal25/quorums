namespace Backend.Core.Domain;

/// <summary>
/// A durable agent run (DL-006). Created by the api as <see cref="RunStatus.Queued"/>
/// and advanced by the worker through the state machine; the supervisor is the sole
/// writer of <see cref="Status"/>.
/// </summary>
public sealed class AgentRun : IBrandScoped
{
    public Guid Id { get; set; }

    public Guid BrandId { get; set; }

    public RunStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
