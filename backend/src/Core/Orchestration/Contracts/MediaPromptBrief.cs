namespace Backend.Core.Orchestration.Contracts;

/// <summary>
/// The structured brief the Media node renders into the Gemini prompt (DL-028). It is a typed
/// object on purpose — not a free-text string — so the one paid external call is guarded:
/// <see cref="AspectRatio"/> is stamped deterministically from the target surface's
/// <c>PlatformConstraints</c> after the Creative Director call (DL-030, DL-034 R8), overriding
/// any model-chosen value before Gemini is ever called.
/// </summary>
public sealed record MediaPromptBrief(
    string Subject,
    string Style,
    string Composition,
    string Palette,
    string Mood,
    string? Negative,
    string AspectRatio);
