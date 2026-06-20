using Backend.Core.Orchestration.Contracts;

namespace Backend.Infrastructure.Integrations.Gemini;

/// <summary>
/// Renders the typed <see cref="MediaPromptBrief"/> into the single prompt string both the Nano-Banana
/// image call and the Veo video call send (DL-058) — one definition so image and video describe the
/// brief identically. The aspect ratio / modality / duration are passed as structured parameters, not
/// prose, so they stay deterministic (DL-030).
/// </summary>
internal static class MediaPromptRenderer
{
    public static string Render(MediaPromptBrief brief)
    {
        ArgumentNullException.ThrowIfNull(brief);

        var prompt =
            $"{brief.Subject}. Style: {brief.Style}. Composition: {brief.Composition}. " +
            $"Palette: {brief.Palette}. Mood: {brief.Mood}.";
        if (!string.IsNullOrWhiteSpace(brief.Negative))
        {
            prompt += $" Avoid: {brief.Negative}.";
        }

        return prompt;
    }
}
