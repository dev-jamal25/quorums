using Backend.Core.Orchestration.Contracts;

namespace Backend.Core.Generation.PlatformConstraints;

/// <summary>
/// Deterministic <c>PlatformConstraints</c> enforcement (DL-030) — never trust the LLM to count.
/// One definition, invoked by both the generation loop and (defense-in-depth) the publish path.
/// Per-constraint remedy is frozen: hashtag-over → <b>repair</b> (truncate); caption-over →
/// <b>regenerate</b> (a validation failure feeds the retry loop) with a hard-truncate fallback;
/// aspectRatio → <b>pre-enforced</b> by stamping the surface's canonical value onto the brief
/// (DL-034 R8), so there is nothing to remedy after the fact.
/// </summary>
public static class PlatformConstraintValidator
{
    // -- hashtagCount: over → repair (truncate + trace note), not regenerate --------------------

    /// <summary>Truncates the hashtag list to the surface limit. Returns the list and whether a repair occurred.</summary>
    public static (IReadOnlyList<string> Hashtags, bool Repaired) RepairHashtags(
        IReadOnlyList<string> hashtags, SurfaceConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(hashtags);
        ArgumentNullException.ThrowIfNull(constraints);

        if (hashtags.Count <= constraints.MaxHashtags)
        {
            return (hashtags, false);
        }

        return (hashtags.Take(constraints.MaxHashtags).ToList(), true);
    }

    /// <summary>Hashtag-count check for the publish-time re-check (the generation path repairs instead).</summary>
    public static ValidationResult ValidateHashtags(
        IReadOnlyList<string> hashtags, SurfaceConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(hashtags);
        ArgumentNullException.ThrowIfNull(constraints);

        return hashtags.Count <= constraints.MaxHashtags
            ? ValidationResult.Valid
            : ValidationResult.Invalid(
                $"hashtags={hashtags.Count}, limit={constraints.MaxHashtags}");
    }

    // -- captionLength: over → regenerate, then hard-truncate fallback --------------------------

    /// <summary>Caption-length check; over-limit is a constraint violation that drives a regenerate.</summary>
    public static ValidationResult ValidateCaptionLength(string caption, SurfaceConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(caption);
        ArgumentNullException.ThrowIfNull(constraints);

        return caption.Length <= constraints.MaxCaptionLength
            ? ValidationResult.Valid
            : ValidationResult.Invalid(
                $"captionLength={caption.Length}, limit={constraints.MaxCaptionLength}");
    }

    /// <summary>The hard-truncate fallback after the bounded retries — the pipeline never emits an over-limit caption.</summary>
    public static string TruncateCaption(string caption, SurfaceConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(caption);
        ArgumentNullException.ThrowIfNull(constraints);

        return caption.Length <= constraints.MaxCaptionLength
            ? caption
            : caption[..constraints.MaxCaptionLength];
    }

    // -- aspectRatio: pre-enforced (stamp), plus a publish-time re-check ------------------------

    /// <summary>
    /// Stamps the surface's canonical aspect ratio onto the brief, overriding any model value (R8).
    /// This is the pre-enforcement: the brief is correct by construction before Gemini is called.
    /// </summary>
    public static MediaPromptBrief StampAspectRatio(MediaPromptBrief brief, SurfaceConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentNullException.ThrowIfNull(constraints);

        return brief with { AspectRatio = constraints.CanonicalAspectRatio };
    }

    // -- modality + duration: pre-enforced (stamp), never model-chosen (DL-058) -----------------

    /// <summary>
    /// Stamps the run's modality (<c>image</c>/<c>video</c>) and, for video, the clip duration onto the
    /// brief — overriding any model value (R8, DL-058), just like the aspect-ratio stamp. The Media node
    /// branches its budget gate and generation on <see cref="MediaPromptBrief.Modality"/>; these are run
    /// inputs, not creative decisions, so the model never picks them.
    /// </summary>
    public static MediaPromptBrief StampVideoFields(MediaPromptBrief brief, string modality, int? durationSec)
    {
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentException.ThrowIfNullOrWhiteSpace(modality);

        return brief with { Modality = modality, DurationSec = durationSec };
    }

    /// <summary>Aspect-ratio check for the publish-time re-check (catches edits made at the human gate).</summary>
    public static ValidationResult ValidateAspectRatio(string aspectRatio, SurfaceConstraints constraints)
    {
        ArgumentNullException.ThrowIfNull(aspectRatio);
        ArgumentNullException.ThrowIfNull(constraints);

        return constraints.AllowedAspectRatios.Contains(aspectRatio, StringComparer.Ordinal)
            ? ValidationResult.Valid
            : ValidationResult.Invalid(
                $"aspectRatio='{aspectRatio}' not in [{string.Join(", ", constraints.AllowedAspectRatios)}]");
    }
}
