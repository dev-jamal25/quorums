namespace Backend.Core.Domain;

/// <summary>
/// Durable, append-only record of a system publish outcome (DL-038, DL-039, DL-040). Keyed by
/// <see cref="ContentItemId"/>, this is the source of truth for the pre-publish idempotency guard
/// ("already published?") — a retried two-step publish must never double-post. Like
/// <see cref="ApprovalAction"/>, these writes are durable Postgres rows, RLS-scoped, and never gated
/// by Langfuse/tracing (DL-040).
/// </summary>
public sealed class PublishRecord : IBrandScoped
{
    public Guid Id { get; set; }

    public Guid BrandId { get; set; }

    public Guid AgentRunId { get; set; }

    /// <summary>The idempotency key the pre-publish guard reads (DL-022, DL-039).</summary>
    public Guid ContentItemId { get; set; }

    /// <summary>
    /// The Meta media-container id, persisted immediately after create and BEFORE publish (DL-039).
    /// Its presence with a null <see cref="ExternalRef"/> means "container created, publish not yet
    /// recorded": re-entry re-publishes the same container (Meta dedups) instead of creating a second
    /// one. Null until the container is created.
    /// </summary>
    public string? CreationId { get; set; }

    public PublishStatus Status { get; set; }

    /// <summary>The published media id; set when <see cref="Status"/> is <see cref="PublishStatus.Published"/>.</summary>
    public string? ExternalRef { get; set; }

    public int AttemptCount { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Engagement-poll handles for the Analytics agent (DL-038, Phase 7); null until live.</summary>
    public EngagementKeys? EngagementKeys { get; set; }
}
