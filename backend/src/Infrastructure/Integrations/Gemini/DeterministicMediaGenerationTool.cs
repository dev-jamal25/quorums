using Backend.Core.Integrations;
using Backend.Core.Orchestration.Contracts;

namespace Backend.Infrastructure.Integrations.Gemini;

/// <summary>
/// Deterministic, network-free <see cref="IMediaGenerationTool"/> (selected by <c>Gemini:Mode=mock</c>):
/// returns a fixed 1×1 PNG regardless of the brief, so CI/compose render media with zero live Gemini
/// calls (CLAUDE.md: CI on mocks only). Idempotency comes from the Media node's deterministic asset
/// key, not from this tool.
/// </summary>
public sealed class DeterministicMediaGenerationTool : IMediaGenerationTool
{
    // Smallest valid PNG: a 1×1 transparent pixel.
    private static readonly byte[] _onePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    public Task<MediaResult> GenerateAsync(
        MediaPromptBrief brief, string modality, CancellationToken cancellationToken = default) =>
        Task.FromResult(new MediaResult(_onePixelPng, "image/png"));
}
