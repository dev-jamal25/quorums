namespace Backend.Core.Onboarding;

/// <summary>
/// The validated brand-identity payload that drives onboarding. Produced from the
/// HTTP request DTO after edge validation, so the domain interior may assume every
/// field is present and well-formed (DL-020: agents/services consume typed records,
/// never free-form input). The brand's display name becomes <see cref="Backend.Core.Domain.Brand.Name"/>;
/// the remaining fields populate the brand-scoped <see cref="Backend.Core.Domain.BrandProfile"/>.
/// </summary>
public sealed record BrandOnboardingCommand(
    string Name,
    string Positioning,
    IReadOnlyList<string> ToneDescriptors,
    IReadOnlyList<string> VoiceDo,
    IReadOnlyList<string> VoiceDont,
    IReadOnlyList<string> ColorHexes,
    string ImageryStyle,
    IReadOnlyList<string> AudienceSegments,
    IReadOnlyList<string> AudiencePainPoints,
    string ProductContext);
