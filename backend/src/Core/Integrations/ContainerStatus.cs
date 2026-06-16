using Backend.Core.Domain;

namespace Backend.Core.Integrations;

/// <summary>
/// The typed result of polling a media container (DL-038, DL-039). <see cref="Processed"/> is true
/// once the container is ready to publish. A container still processing is a
/// <see cref="PublishStatus.TransientFailure"/> in <see cref="Failure"/> (retry the poll); a
/// terminal condition (invalid media) is a <see cref="PublishStatus.TerminalFailure"/>.
/// </summary>
public sealed record ContainerStatus(bool Processed, PublishStatus? Failure, string? Error);
