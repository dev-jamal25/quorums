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

    /// <summary>
    /// Advances the run to <paramref name="target"/> through the central transition guard
    /// (<see cref="RunStatusTransition"/>) and stamps <see cref="UpdatedAt"/>. This is the ONLY
    /// sanctioned way to change <see cref="Status"/> after creation — raw assignments are banned
    /// (DL-006, DL-036, DL-037).
    /// </summary>
    /// <exception cref="InvalidRunStatusTransitionException">The edge is not permitted.</exception>
    public void TransitionTo(RunStatus target, DateTimeOffset occurredAt)
    {
        if (!RunStatusTransition.IsAllowed(Status, target))
        {
            throw new InvalidRunStatusTransitionException(Status, target);
        }

        Status = target;
        UpdatedAt = occurredAt;
    }
}
