using Backend.Core.Orchestration.Contracts;

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

    /// <summary>
    /// The per-run content modality (DL-058 Decision 1), chosen on <c>POST /runs</c> and read by
    /// <c>ExecuteRun</c> into <c>RunState</c>. Persisted here — NOT in the Hangfire payload — so a retry
    /// rebuilds the same modality through Postgres (DL-006). Defaults to <see cref="Modality.Image"/>.
    /// </summary>
    public Modality Modality { get; set; } = Modality.Image;

    /// <summary>
    /// The video source for a <see cref="Modality.Video"/> run (DL-058): image-seed (default) vs
    /// text-to-video. Null for an image run.
    /// </summary>
    public VideoSource? VideoSource { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// The Hangfire job id of the delayed <c>ResumeRun</c> when this run is <see cref="RunStatus.Scheduled"/>
    /// (DL-037). Stored so cancel can <c>BackgroundJob.Delete</c> the pending job deterministically; null
    /// outside the scheduled window. A run-level infrastructure correlation, like <see cref="Status"/> —
    /// not orchestration state (distinct from the Supervisor-owned <c>RunState</c>, DL-020).
    /// </summary>
    public string? ScheduledJobId { get; set; }

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
