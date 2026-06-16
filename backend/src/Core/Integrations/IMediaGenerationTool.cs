using Backend.Core.Orchestration.Contracts;

namespace Backend.Core.Integrations;

/// <summary>
/// The media-generation seam (DL-001: Gemini is a media <em>tool</em>, never an orchestrator). The
/// Media node renders a <see cref="MediaPromptBrief"/> into a generated asset through this interface.
/// CI/compose run on <c>DeterministicMediaGenerationTool</c> (a fixed image, selected by
/// <c>Gemini:Mode=mock</c>); the live Gemini HTTP client sits behind the same seam (a later step).
/// A failure surfaces as a thrown exception the Media node catches into a structured <c>ToolError</c>
/// — never an exception into the graph (DL-022).
/// </summary>
public interface IMediaGenerationTool
{
    Task<MediaResult> GenerateAsync(
        MediaPromptBrief brief,
        string modality,
        CancellationToken cancellationToken = default);
}

/// <summary>The generated asset bytes and their MIME type, ready for the idempotent storage write.</summary>
public sealed record MediaResult(byte[] Bytes, string MimeType);
