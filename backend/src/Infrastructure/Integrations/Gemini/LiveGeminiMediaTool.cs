using Backend.Core.Integrations;
using Backend.Core.Orchestration.Contracts;

namespace Backend.Infrastructure.Integrations.Gemini;

/// <summary>
/// Present-but-throwing live Gemini seam (selected by <c>Gemini:Mode=live</c>), mirroring
/// <c>LiveMetaIntegration</c>. The real Gemini HTTP client is a separate step (P3); until then
/// selecting <c>live</c> fails fast rather than silently no-op'ing. The Media node catches the
/// throw into a structured <c>ToolError</c> — never an exception into the graph (DL-022).
/// </summary>
public sealed class LiveGeminiMediaTool : IMediaGenerationTool
{
    public Task<MediaResult> GenerateAsync(
        MediaPromptBrief brief, string modality, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(
            "Live Gemini media generation is not implemented yet (P3). Set Gemini:Mode=mock for CI/demo.");
}
