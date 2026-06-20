using Backend.Api.Dtos;
using Backend.Core.Orchestration.Contracts;
using Xunit;

namespace Backend.UnitTests.Api;

/// <summary>
/// Boundary validation of run-create modality selection (DL-058). The only cross-field rule:
/// <c>videoSource</c> is valid ONLY for a Video run; supplying it with an Image (or omitted) modality is
/// a 400 (the <c>[ApiController]</c> auto-400 short-circuits before the action). A Video run may omit
/// <c>videoSource</c> (the controller defaults it to <see cref="VideoSource.ImageSeed"/>), and an empty
/// body (all null → an Image run) is valid.
/// </summary>
public sealed class CreateRunRequestValidatorTests
{
    private static readonly CreateRunRequestValidator _validator = new();

    [Fact]
    public void Image_with_a_video_source_is_invalid()
    {
        var request = new CreateRunRequest(Modality.Image, VideoSource.ImageSeed);
        Assert.False(_validator.Validate(request).IsValid);
    }

    [Fact]
    public void Omitted_modality_with_a_video_source_is_invalid()
    {
        // No modality (defaults to Image at the controller) must not carry a videoSource.
        var request = new CreateRunRequest(Modality: null, VideoSource: VideoSource.TextPrompt);
        Assert.False(_validator.Validate(request).IsValid);
    }

    [Fact]
    public void Image_alone_is_valid()
    {
        Assert.True(_validator.Validate(new CreateRunRequest(Modality.Image)).IsValid);
    }

    [Fact]
    public void Empty_body_equivalent_is_valid()
    {
        // All-null is what an empty POST body binds to (then the controller resolves Image).
        Assert.True(_validator.Validate(new CreateRunRequest()).IsValid);
    }

    [Fact]
    public void Video_without_a_video_source_is_valid()
    {
        // The controller defaults the source to ImageSeed; the validator must not require it.
        Assert.True(_validator.Validate(new CreateRunRequest(Modality.Video)).IsValid);
    }

    [Theory]
    [InlineData(VideoSource.ImageSeed)]
    [InlineData(VideoSource.TextPrompt)]
    public void Video_with_a_video_source_is_valid(VideoSource source)
    {
        Assert.True(_validator.Validate(new CreateRunRequest(Modality.Video, source)).IsValid);
    }
}
