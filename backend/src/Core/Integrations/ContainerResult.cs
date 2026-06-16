using Backend.Core.Domain;

namespace Backend.Core.Integrations;

/// <summary>
/// The typed result of the create-container step (DL-038, DL-039). On success <see cref="CreationId"/>
/// is set and <see cref="Failure"/> is null; on failure <see cref="Failure"/> carries the classified
/// outcome (<see cref="PublishStatus.TransientFailure"/> / <see cref="PublishStatus.TerminalFailure"/>)
/// — the caller classifies from this, never by exception-sniffing.
/// </summary>
public sealed record ContainerResult(string? CreationId, PublishStatus? Failure, string? Error);
