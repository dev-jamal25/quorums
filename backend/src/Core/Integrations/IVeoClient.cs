namespace Backend.Core.Integrations;

/// <summary>
/// The narrow Veo 3.1 long-running-operation seam (DL-058) the live video path drives:
/// <see cref="SubmitAsync"/> starts a generation and returns the operation <b>name</b> (not the video),
/// <see cref="PollAsync"/> reports its status, and <see cref="DownloadAsync"/> fetches the finished mp4.
/// Splitting submit from poll/download is what makes the async core <b>submit-or-resume idempotent</b>:
/// the in-flight operation name is recorded the instant <see cref="SubmitAsync"/> returns, so a retry
/// resumes by <see cref="PollAsync"/> without re-billing a paid clip. The live HTTP implementation lives
/// in Infrastructure; tests substitute a fake exposing a submit counter (the audit-#1 proof).
/// </summary>
public interface IVeoClient
{
    /// <summary>Starts a Veo generation; returns the long-running operation name (e.g. <c>models/.../operations/...</c>).</summary>
    Task<string> SubmitAsync(VeoSubmitRequest request, CancellationToken cancellationToken = default);

    /// <summary>Polls one operation once; non-blocking — the caller owns the bounded poll loop.</summary>
    Task<VeoOperation> PollAsync(string operationName, CancellationToken cancellationToken = default);

    /// <summary>Downloads the finished mp4 from the operation's result URI (same api-key auth).</summary>
    Task<byte[]> DownloadAsync(string downloadUri, CancellationToken cancellationToken = default);
}

/// <summary>
/// One Veo generation request. <paramref name="SeedImage"/> is the Nano-Banana first frame for
/// <c>ImageSeed</c> (null for text-to-video). <paramref name="AspectRatio"/> is 9:16 from the reel
/// surface; <paramref name="DurationSec"/> is capped at <c>Veo:MaxDurationSec</c>; <paramref name="Model"/>
/// is <c>Veo:Model</c>. No secret travels here — the api key is on the client's header.
/// </summary>
public sealed record VeoSubmitRequest(
    string Prompt,
    SeedImage? SeedImage,
    string AspectRatio,
    int DurationSec,
    string Model);

/// <summary>An inline first-frame image handed to Veo (raw bytes + MIME).</summary>
public sealed record SeedImage(byte[] Bytes, string MimeType);

/// <summary>A single poll result: terminal state plus the download URI (succeeded) or error (failed).</summary>
public sealed record VeoOperation(VeoOperationStatus Status, string? DownloadUri = null, string? Error = null);

/// <summary>Veo operation lifecycle: still running, finished with a video, or terminally failed.</summary>
public enum VeoOperationStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2,
}
