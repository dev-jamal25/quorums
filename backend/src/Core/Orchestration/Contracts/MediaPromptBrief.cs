namespace Backend.Core.Orchestration.Contracts;

/// <summary>
/// The structured brief the Media node renders into the Gemini/Veo prompt (DL-028, DL-058). It is a
/// typed object on purpose — not a free-text string — so the one paid external call is guarded:
/// <see cref="AspectRatio"/>, <see cref="Modality"/>, and <see cref="DurationSec"/> are stamped
/// deterministically from the run + target surface after the Creative Director call (DL-030, DL-034
/// R8, DL-058), overriding any model-chosen value before Gemini/Veo is ever called. <c>Modality</c> is
/// <c>image</c> (default) or <c>video</c>; <c>DurationSec</c> is the video clip length (null for image),
/// capped at <c>Veo:MaxDurationSec</c> — both stamped from the run, never model-chosen.
/// </summary>
public sealed record MediaPromptBrief(
    string Subject,
    string Style,
    string Composition,
    string Palette,
    string Mood,
    string? Negative,
    string AspectRatio,
    string Modality = "image",
    int? DurationSec = null);
