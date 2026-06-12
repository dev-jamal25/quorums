using Backend.Core.Domain;
using Backend.Core.Orchestration;

namespace Backend.Api.Dtos;

public sealed record RunStatusResponse(
    Guid RunId,
    RunStatus Status,
    GraphPhase? Phase);
