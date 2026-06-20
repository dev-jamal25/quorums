using Backend.Core.Integrations;
using Backend.Core.Orchestration.Contracts;
using Backend.Infrastructure.Configuration.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backend.Infrastructure.Integrations.Gemini;

/// <summary>
/// The Veo async core (DL-058, gotcha #1): <b>submit-or-resume idempotent</b> over a paid, asynchronous
/// long-running operation. On entry it consults the <see cref="VeoOperationStore"/> keyed by the
/// deterministic <c>assetId</c>:
/// <list type="bullet">
///   <item><b>hit</b> → resume polling the recorded operation; <b>never</b> submit again (a node retry
///   bills zero new Veo jobs).</item>
///   <item><b>miss</b> → submit, record the operation name in the store <b>before any polling</b>, then poll.</item>
/// </list>
/// Polling is bounded by <c>Veo:PollTimeout</c>; a timeout or a terminal Veo error throws
/// <see cref="VeoGenerationException"/>, which the Media node degrades to caption-only (DL-022/023) — this
/// class never returns a partial result. On success it downloads the mp4 and returns it; the node commits
/// it to storage and then evicts the store entry (eviction is node-side, after commit).
/// </summary>
public sealed partial class VeoVideoGenerator
{
    private readonly IVeoClient _client;
    private readonly VeoOperationStore _store;
    private readonly VeoOptions _options;
    private readonly ILogger<VeoVideoGenerator> _logger;

    public VeoVideoGenerator(
        IVeoClient client,
        VeoOperationStore store,
        IOptions<VeoOptions> options,
        ILogger<VeoVideoGenerator> logger)
    {
        _client = client;
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Generates (or resumes) one Veo clip for <paramref name="assetId"/> and returns the mp4 bytes +
    /// duration. Builds the Veo request from the brief (prompt, 9:16, duration capped at
    /// <c>Veo:MaxDurationSec</c>, <c>Veo:Model</c>) and the optional <paramref name="seedImage"/>
    /// first frame (image-seed); null seed = text-to-video.
    /// </summary>
    public async Task<MediaResult> GenerateAsync(
        MediaPromptBrief brief, SeedImage? seedImage, Guid assetId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(brief);

        var duration = Math.Clamp(brief.DurationSec ?? _options.MaxDurationSec, 1, _options.MaxDurationSec);
        var submit = new VeoSubmitRequest(
            Prompt: MediaPromptRenderer.Render(brief),
            SeedImage: seedImage,
            AspectRatio: brief.AspectRatio,
            DurationSec: duration,
            Model: _options.Model);

        // Submit-or-resume: the store is the single source of truth for an in-flight op on this asset.
        string operationName;
        if (_store.TryGet(assetId, out var existing) && !string.IsNullOrEmpty(existing))
        {
            operationName = existing;
            LogResume(assetId, operationName);
        }
        else
        {
            operationName = await _client.SubmitAsync(submit, cancellationToken).ConfigureAwait(false);
            // Record BEFORE polling — a crash/retry between submit and the first poll must still resume.
            _store.Set(assetId, operationName);
            LogSubmit(assetId, operationName);
        }

        // Bounded poll loop (Veo:PollTimeout). Never loop unbounded; never hang.
        var deadline = DateTimeOffset.UtcNow + _options.PollTimeout;
        while (true)
        {
            var operation = await _client.PollAsync(operationName, cancellationToken).ConfigureAwait(false);

            switch (operation.Status)
            {
                case VeoOperationStatus.Succeeded:
                    if (string.IsNullOrEmpty(operation.DownloadUri))
                    {
                        throw new VeoGenerationException(
                            $"Veo operation '{operationName}' reported done with no video URI.");
                    }

                    var bytes = await _client.DownloadAsync(operation.DownloadUri, cancellationToken)
                        .ConfigureAwait(false);
                    LogSucceeded(operationName, bytes.Length);
                    return new MediaResult(bytes, "video/mp4", submit.DurationSec);

                case VeoOperationStatus.Failed:
                    throw new VeoGenerationException(
                        $"Veo operation '{operationName}' failed terminally: {operation.Error}");

                default: // Pending
                    var remaining = deadline - DateTimeOffset.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        throw new VeoGenerationException(
                            $"Veo operation '{operationName}' did not finish within {_options.PollTimeout}.");
                    }

                    // Don't overshoot the deadline on the last wait.
                    var wait = _options.PollInterval < remaining ? _options.PollInterval : remaining;
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Veo: submitted operation {OperationName} for asset {AssetId}.")]
    private partial void LogSubmit(Guid assetId, string operationName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Veo: resuming in-flight operation {OperationName} for asset {AssetId} (no new submit).")]
    private partial void LogResume(Guid assetId, string operationName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Veo: operation {OperationName} succeeded ({ByteCount} mp4 bytes).")]
    private partial void LogSucceeded(string operationName, int byteCount);
}
