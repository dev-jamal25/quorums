using Backend.Core.Domain;

namespace Backend.Core.Orchestration.Contracts;

/// <summary>
/// The typed outcome of a publish (DL-038, DL-039). <see cref="Status"/> classifies the result
/// (<see cref="PublishStatus.Published"/> / <see cref="PublishStatus.TransientFailure"/> /
/// <see cref="PublishStatus.TerminalFailure"/>) — callers branch on it, never on exception type.
/// <see cref="ExternalRef"/> is the published media id (set when published); <see cref="Error"/> is
/// surfaced to the reviewer on terminal failure; <see cref="EngagementKeys"/> are the poll handles
/// the Analytics agent reads later (Phase 7).
/// </summary>
public sealed record PublishResult(
    PublishStatus Status,
    string? ExternalRef,
    string? Error,
    EngagementKeys? EngagementKeys);
