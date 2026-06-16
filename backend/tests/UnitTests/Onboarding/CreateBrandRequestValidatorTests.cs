using Backend.Api.Dtos;
using FluentValidation.Results;
using Xunit;

namespace Backend.UnitTests.Onboarding;

/// <summary>
/// Edge-validation tests for the onboarding DTO. The boundary is the one place that
/// rejects malformed input; everything downstream trusts the validated shape.
/// </summary>
public sealed class CreateBrandRequestValidatorTests
{
    private readonly CreateBrandRequestValidator _validator = new();

    private static CreateBrandRequest ValidRequest() => new()
    {
        Name = "Lumen Coffee",
        Positioning = "Specialty coffee for people who hate fuss.",
        ToneDescriptors = ["warm", "direct"],
        VoiceDo = ["speak plainly"],
        VoiceDont = ["no hype words"],
        ColorHexes = ["#1A2B3C", "#FFF"],
        ImageryStyle = "Bright, minimal, natural light.",
        ContentPillars = ["Origin", "Craft", "Ritual"],
        AudienceSegments = ["urban professionals"],
        AudiencePainPoints = ["bad office coffee"],
        ProductContext = "Single-origin beans sold by subscription.",
    };

    private static bool HasErrorFor(ValidationResult result, string propertyName) =>
        result.Errors.Exists(error => error.PropertyName == propertyName);

    [Fact]
    public void Fully_populated_request_is_valid()
    {
        var result = _validator.Validate(ValidRequest());

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_name_is_rejected(string name)
    {
        var result = _validator.Validate(CloneWith(ValidRequest(), name: name));

        Assert.False(result.IsValid);
        Assert.True(HasErrorFor(result, nameof(CreateBrandRequest.Name)));
    }

    [Fact]
    public void Blank_positioning_is_rejected()
    {
        var result = _validator.Validate(CloneWith(ValidRequest(), positioning: "  "));

        Assert.True(HasErrorFor(result, nameof(CreateBrandRequest.Positioning)));
    }

    [Fact]
    public void Empty_tone_descriptors_is_rejected()
    {
        var result = _validator.Validate(CloneWith(ValidRequest(), toneDescriptors: []));

        Assert.True(HasErrorFor(result, nameof(CreateBrandRequest.ToneDescriptors)));
    }

    [Fact]
    public void Blank_tone_descriptor_element_is_rejected()
    {
        var result = _validator.Validate(CloneWith(ValidRequest(), toneDescriptors: ["warm", "  "]));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Malformed_color_hex_is_rejected()
    {
        var result = _validator.Validate(CloneWith(ValidRequest(), colorHexes: ["#1A2B3C", "tan"]));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Empty_color_hexes_is_rejected()
    {
        var result = _validator.Validate(CloneWith(ValidRequest(), colorHexes: []));

        Assert.True(HasErrorFor(result, nameof(CreateBrandRequest.ColorHexes)));
    }

    [Fact]
    public void Blank_imagery_style_is_rejected()
    {
        var result = _validator.Validate(CloneWith(ValidRequest(), imageryStyle: ""));

        Assert.True(HasErrorFor(result, nameof(CreateBrandRequest.ImageryStyle)));
    }

    [Fact]
    public void Empty_content_pillars_is_rejected()
    {
        var result = _validator.Validate(CloneWith(ValidRequest(), contentPillars: []));

        Assert.True(HasErrorFor(result, nameof(CreateBrandRequest.ContentPillars)));
    }

    [Fact]
    public void Blank_content_pillar_element_is_rejected()
    {
        var result = _validator.Validate(CloneWith(ValidRequest(), contentPillars: ["Origin", "  "]));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Empty_audience_segments_is_rejected()
    {
        var result = _validator.Validate(CloneWith(ValidRequest(), audienceSegments: []));

        Assert.True(HasErrorFor(result, nameof(CreateBrandRequest.AudienceSegments)));
    }

    [Fact]
    public void Empty_audience_pain_points_is_rejected()
    {
        var result = _validator.Validate(CloneWith(ValidRequest(), audiencePainPoints: []));

        Assert.True(HasErrorFor(result, nameof(CreateBrandRequest.AudiencePainPoints)));
    }

    [Fact]
    public void Blank_product_context_is_rejected()
    {
        var result = _validator.Validate(CloneWith(ValidRequest(), productContext: " "));

        Assert.True(HasErrorFor(result, nameof(CreateBrandRequest.ProductContext)));
    }

    private static CreateBrandRequest CloneWith(
        CreateBrandRequest source,
        string? name = null,
        string? positioning = null,
        IReadOnlyList<string>? toneDescriptors = null,
        IReadOnlyList<string>? colorHexes = null,
        string? imageryStyle = null,
        IReadOnlyList<string>? contentPillars = null,
        IReadOnlyList<string>? audienceSegments = null,
        IReadOnlyList<string>? audiencePainPoints = null,
        string? productContext = null) => new()
        {
            Name = name ?? source.Name,
            Positioning = positioning ?? source.Positioning,
            ToneDescriptors = toneDescriptors ?? source.ToneDescriptors,
            VoiceDo = source.VoiceDo,
            VoiceDont = source.VoiceDont,
            ColorHexes = colorHexes ?? source.ColorHexes,
            ImageryStyle = imageryStyle ?? source.ImageryStyle,
            ContentPillars = contentPillars ?? source.ContentPillars,
            AudienceSegments = audienceSegments ?? source.AudienceSegments,
            AudiencePainPoints = audiencePainPoints ?? source.AudiencePainPoints,
            ProductContext = productContext ?? source.ProductContext,
        };
}
