namespace Backend.Core.Orchestration.Contracts;

/// <summary>
/// The Creative Director's output — owns <em>how it looks</em> (DL-027/028): the visual concept,
/// style and colour tokens, and the structured <see cref="Contracts.MediaPromptBrief"/> (the only
/// instruction the Media node receives). Every output carries <see cref="Contracts.Grounding"/>.
/// </summary>
public sealed record CreativeDirection(
    string VisualConcept,
    IReadOnlyList<string> StyleTokens,
    IReadOnlyList<ColorToken> ColorTokens,
    MediaPromptBrief MediaPromptBrief,
    Grounding Grounding);
