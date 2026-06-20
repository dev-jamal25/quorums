using Backend.Core.Domain;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;

namespace Backend.Api.Dtos;

/// <summary>The run's status + graph phase, plus its modality (DL-058) so a client can render video vs image.</summary>
public sealed record RunStatusResponse(
    Guid RunId,
    RunStatus Status,
    GraphPhase? Phase,
    Modality Modality,
    VideoSource? VideoSource);
