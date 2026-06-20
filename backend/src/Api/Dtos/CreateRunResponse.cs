using Backend.Core.Orchestration.Contracts;

namespace Backend.Api.Dtos;

/// <summary>The accepted run id plus the resolved modality (DL-058), so a client can confirm its selection.</summary>
public sealed record CreateRunResponse(Guid RunId, Modality Modality, VideoSource? VideoSource);
