using System.Text.Json.Serialization;

namespace Backend.Core.Orchestration.Contracts;

/// <summary>
/// How a video run feeds Veo (DL-058). <see cref="ImageSeed"/> (default) generates the Nano-Banana
/// image first and animates it as Veo's first frame (brand-consistent — the run pays for the image AND
/// the video); <see cref="TextPrompt"/> is text-to-video directly from the <see cref="MediaPromptBrief"/>.
/// Ignored for an image run. Binds from / serializes to its JSON string name via the targeted converter
/// (matching the global <c>RunStateJsonOptions</c> string-enum convention) so the <c>POST /runs</c> body
/// accepts <c>"ImageSeed"</c>/<c>"TextPrompt"</c> with no global API JSON change.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoSource
{
    ImageSeed = 0,
    TextPrompt = 1,
}
