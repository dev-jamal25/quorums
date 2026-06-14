using Backend.Core.Common;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Backend.Core.Storage;
using Microsoft.Agents.AI.Workflows;

namespace Backend.Infrastructure.Orchestration.Maf.Nodes;

/// <summary>
/// Media Generation node (DL-019): owns the media asset. Forks in parallel with Copywriting.
/// Deterministic stub: writes a real 1×1 PNG to storage and returns a
/// <see cref="MediaAssetRef"/>. The asset id is derived from the run id, so a retried
/// segment overwrites the same key — the side effect is idempotent (DL-022). A storage
/// failure degrades to a structured <see cref="ToolError"/> on the slice rather than
/// throwing into the graph; the supervisor adjudicates downstream. No Gemini (later slice).
/// </summary>
public sealed class MediaGenerationExecutor : Executor<RunState, RunState>
{
    // Smallest valid PNG: a 1×1 transparent pixel. Deterministic placeholder media.
    private static readonly byte[] _onePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private const string MediaMimeType = "image/png";

    private readonly IStorageService _storage;
    private readonly ITrace _trace;

    public MediaGenerationExecutor(IStorageService storage, ITrace trace)
        : base("media-generation")
    {
        _storage = storage;
        _trace = trace;
    }

    public override ValueTask<RunState> HandleAsync(
        RunState message, IWorkflowContext context, CancellationToken cancellationToken = default)
        => new(RunAsync(message, cancellationToken));

    public async Task<RunState> RunAsync(RunState state, CancellationToken cancellationToken = default)
    {
        // Asset id is deterministic per run so a retried segment overwrites the same
        // storage key (idempotent side effect, DL-022).
        var assetId = DeterministicGuid.From(state.RunId, "asset");
        var key = StorageKeys.ForAsset(state.BrandId, assetId, "png");

        MediaAssetRef? media = null;
        var errors = state.Errors;

        var startedAt = DateTimeOffset.UtcNow;
        string status;
        string? error = null;
        try
        {
            var storedKey = await _storage
                .PutAsync(key, _onePixelPng, MediaMimeType, cancellationToken)
                .ConfigureAwait(false);

            media = new MediaAssetRef(
                AssetId: assetId,
                StorageKey: storedKey,
                Modality: "image",
                MimeType: MediaMimeType);
            status = "ok";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Degrade, don't crash: record a structured tool error and continue.
            errors = [.. state.Errors, new ToolError(
                Code: "storage.put_failed",
                Message: ex.Message,
                Retryable: true)];
            status = "error";
            error = ex.Message;
        }

        var trace = await _trace.RecordAsync(
            state.Trace, state.RunId, state.BrandId, "media", "minio.put",
            status, startedAt, DateTimeOffset.UtcNow, error, cancellationToken)
            .ConfigureAwait(false);

        return state with { Media = media, Errors = errors, Trace = trace };
    }
}
