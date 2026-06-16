namespace Backend.Core.Orchestration.Contracts;

/// <summary>
/// The Copywriting output — the caption (DL-027/028): hook, body, and hashtags. The hashtag count
/// and caption length are checked against the surface's <c>PlatformConstraints</c> (DL-030) after
/// generation. Every output carries <see cref="Contracts.Grounding"/>.
/// </summary>
public sealed record Caption(
    string Hook,
    string Body,
    IReadOnlyList<string> Hashtags,
    Grounding Grounding);
