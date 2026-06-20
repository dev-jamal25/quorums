using Backend.Core.Orchestration.Contracts;
using FluentValidation;

namespace Backend.Api.Dtos;

/// <summary>
/// Fail-fast boundary validation for run-create modality selection (DL-058). The only cross-field rule:
/// <see cref="VideoSource"/> is meaningful ONLY for a video run, so supplying it with an image (or omitted)
/// modality is a 400 ProblemDetails. A video run may omit <see cref="VideoSource"/> (the controller defaults
/// it to <see cref="VideoSource.ImageSeed"/>). Invalid enum values are rejected earlier by JSON binding.
/// </summary>
public sealed class CreateRunRequestValidator : AbstractValidator<CreateRunRequest>
{
    public CreateRunRequestValidator()
    {
        RuleFor(r => r.VideoSource)
            .Null()
            .When(r => r.Modality is null or Modality.Image)
            .WithMessage("videoSource is only valid when modality is Video.");
    }
}
