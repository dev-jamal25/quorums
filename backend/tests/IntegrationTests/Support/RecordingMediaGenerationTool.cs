using Backend.Core.Integrations;
using Backend.Infrastructure.Integrations.Gemini;

namespace Backend.IntegrationTests.Support;

/// <summary>
/// Wraps the deterministic media tool and counts calls — so the slice-proof and ceiling tests can
/// assert <b>zero</b> <see cref="IMediaGenerationTool"/> calls when the budget gate degrades/fails
/// before generation (R1/R2). An optional <c>throwOnCall</c> drives the Gemini-failure → fatal path.
/// </summary>
internal sealed class RecordingMediaGenerationTool : IMediaGenerationTool
{
    private readonly DeterministicMediaGenerationTool _inner = new();
    private readonly bool _throwOnCall;

    public RecordingMediaGenerationTool(bool throwOnCall = false) => _throwOnCall = throwOnCall;

    public int Calls { get; private set; }

    public MediaGenerationRequest? LastRequest { get; private set; }

    public Task<MediaResult> GenerateAsync(
        MediaGenerationRequest request, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastRequest = request;
        if (_throwOnCall)
        {
            throw new InvalidOperationException("simulated Gemini failure");
        }

        return _inner.GenerateAsync(request, cancellationToken);
    }
}
