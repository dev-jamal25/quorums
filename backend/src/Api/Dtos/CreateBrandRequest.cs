namespace Backend.Api.Dtos;

/// <summary>
/// The onboarding request body for <c>POST /brands</c>: the structured brand
/// identity a new tenant is created from. Validated at the edge by
/// <see cref="CreateBrandRequestValidator"/> (invalid → automatic 400 ProblemDetails
/// via <c>[ApiController]</c>); the domain interior trusts it thereafter. The brand
/// id is NOT accepted from the caller — onboarding generates it.
/// </summary>
public sealed class CreateBrandRequest
{
    /// <summary>The brand's display name (becomes the identity name on the Brand row).</summary>
    public string Name { get; init; } = default!;

    /// <summary>Positioning statement / tagline.</summary>
    public string Positioning { get; init; } = default!;

    /// <summary>Tone descriptors characterising the brand voice.</summary>
    public IReadOnlyList<string> ToneDescriptors { get; init; } = [];

    /// <summary>"Do" language guidance for the voice.</summary>
    public IReadOnlyList<string> VoiceDo { get; init; } = [];

    /// <summary>"Don't" language guidance for the voice.</summary>
    public IReadOnlyList<string> VoiceDont { get; init; } = [];

    /// <summary>Brand color palette as hex strings (e.g. "#1A2B3C").</summary>
    public IReadOnlyList<string> ColorHexes { get; init; } = [];

    /// <summary>Description of the brand's imagery style.</summary>
    public string ImageryStyle { get; init; } = default!;

    /// <summary>Target audience segments.</summary>
    public IReadOnlyList<string> AudienceSegments { get; init; } = [];

    /// <summary>Audience pain points the brand speaks to.</summary>
    public IReadOnlyList<string> AudiencePainPoints { get; init; } = [];

    /// <summary>What the brand sells and the context agents need to ground content.</summary>
    public string ProductContext { get; init; } = default!;
}
