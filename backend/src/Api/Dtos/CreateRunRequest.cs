using Backend.Core.Orchestration.Contracts;

namespace Backend.Api.Dtos;

/// <summary>
/// The optional <c>POST /runs</c> body that selects the content modality (DL-058 Decision 1). Both fields
/// are optional: an absent body — or an absent <see cref="Modality"/> — is an <see cref="Modality.Image"/>
/// run, exactly as before (no regression). For a <see cref="Modality.Video"/> run, <see cref="VideoSource"/>
/// is optional and defaults to <see cref="VideoSource.ImageSeed"/> (the DL-058 default) in the controller.
/// Enums bind from their JSON string names via the targeted converters on the enums themselves.
/// </summary>
public sealed record CreateRunRequest(
    Modality? Modality = null,
    VideoSource? VideoSource = null);
