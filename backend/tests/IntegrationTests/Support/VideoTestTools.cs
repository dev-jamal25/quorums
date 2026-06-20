using Backend.Core.Integrations;
using Backend.Infrastructure.Integrations.Gemini;

namespace Backend.IntegrationTests.Support;

/// <summary>
/// A scriptable fake <see cref="IVeoClient"/> for the DL-058 proofs: a submit counter (to assert ZERO
/// re-submits on a node retry) and a poll outcome that either succeeds, never finishes (timeout proof),
/// or fails terminally.
/// </summary>
internal sealed class FakeVeoClient : IVeoClient
{
    private readonly bool _neverCompletes;
    private readonly bool _failTerminally;

    public FakeVeoClient(bool neverCompletes = false, bool failTerminally = false)
    {
        _neverCompletes = neverCompletes;
        _failTerminally = failTerminally;
    }

    public int SubmitCount { get; private set; }

    public Task<string> SubmitAsync(VeoSubmitRequest request, CancellationToken cancellationToken = default)
    {
        SubmitCount++;
        return Task.FromResult($"models/veo/operations/op-{SubmitCount}");
    }

    public Task<VeoOperation> PollAsync(string operationName, CancellationToken cancellationToken = default)
    {
        if (_failTerminally)
        {
            return Task.FromResult(new VeoOperation(VeoOperationStatus.Failed, Error: "terminal"));
        }

        return Task.FromResult(_neverCompletes
            ? new VeoOperation(VeoOperationStatus.Pending)
            : new VeoOperation(VeoOperationStatus.Succeeded, DownloadUri: "https://veo.example/clip.mp4"));
    }

    public Task<byte[]> DownloadAsync(string downloadUri, CancellationToken cancellationToken = default) =>
        Task.FromResult<byte[]>([0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70]); // a stand-in mp4 (ftyp)
}

/// <summary>
/// An <see cref="IMediaGenerationTool"/> whose video path drives the real <see cref="VeoVideoGenerator"/>
/// (so the graph proofs exercise the genuine submit-or-resume + bounded-poll async core), and whose image
/// path falls back to the deterministic image. Image-seed seeding is irrelevant to these proofs, so the
/// seed frame is omitted (the fake client ignores it).
/// </summary>
internal sealed class VeoBackedMediaTool : IMediaGenerationTool
{
    private const string VideoModality = "video";
    private readonly VeoVideoGenerator _veo;
    private readonly DeterministicMediaGenerationTool _image = new();

    public VeoBackedMediaTool(VeoVideoGenerator veo) => _veo = veo;

    public Task<MediaResult> GenerateAsync(
        MediaGenerationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return string.Equals(request.Brief.Modality, VideoModality, StringComparison.OrdinalIgnoreCase)
            ? _veo.GenerateAsync(request.Brief, seedImage: null, request.AssetId, cancellationToken)
            : _image.GenerateAsync(request, cancellationToken);
    }
}
