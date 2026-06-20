using Backend.Core.Orchestration.Contracts;

namespace Backend.Core.Integrations;

/// <summary>
/// The media-generation seam (DL-001: Gemini/Veo is a media <em>tool</em>, never an orchestrator). The
/// Media node renders a <see cref="MediaPromptBrief"/> into a generated asset through this interface —
/// an image (Nano Banana) or a video (Veo 3.1), chosen by <see cref="MediaPromptBrief.Modality"/>
/// (DL-058). CI/compose run on <c>DeterministicMediaGenerationTool</c> (a fixed asset, selected by
/// <c>Gemini:Mode=mock</c>); the live Gemini/Veo HTTP client sits behind the same seam.
/// A failure surfaces as a thrown exception the Media node catches into a structured <c>ToolError</c>
/// — never an exception into the graph (DL-022). The <see cref="MediaGenerationRequest.AssetId"/> is the
/// deterministic key the video path uses for submit-or-resume idempotency (DL-058).
/// </summary>
public interface IMediaGenerationTool
{
    Task<MediaResult> GenerateAsync(
        MediaGenerationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Media node's request to the tool: the brief (carries modality + duration), the deterministic
/// <paramref name="AssetId"/> (= <c>DeterministicGuid.From(runId,"asset")</c>) the video path keys its
/// in-flight operation on, and the <paramref name="Source"/> for video (image-seed vs text-to-video).
/// </summary>
public sealed record MediaGenerationRequest(
    MediaPromptBrief Brief,
    Guid AssetId,
    VideoSource Source = VideoSource.ImageSeed);

/// <summary>The generated asset bytes + MIME type (and clip length for video), ready for the idempotent storage write.</summary>
public sealed record MediaResult(byte[] Bytes, string MimeType, int? DurationSec = null);
