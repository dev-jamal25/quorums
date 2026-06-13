using Backend.Core.Common;
using Backend.Core.Integrations;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Backend.Core.Storage;

namespace Backend.Infrastructure.Orchestration;

/// <summary>
/// Deterministic placeholder orchestrator (no LLM, no MAF). It produces fixed typed
/// outputs but performs <em>real</em> I/O on the proven durable seam: the media step
/// writes a real object to MinIO and the publish step goes through
/// <see cref="IMetaIntegration"/>. Every node and every tool call records a span via
/// <see cref="ITrace"/>, threaded through <see cref="RunState.Trace"/> so the trace
/// survives the pause/resume seam. A storage or publish failure surfaces as a
/// structured <see cref="ToolError"/> on <see cref="RunState.Errors"/> (DL-022)
/// rather than an exception into the graph; the asset id and publish key are derived
/// from the run id so a Hangfire retry overwrites the same key / re-uses the same
/// external ref instead of duplicating.
/// </summary>
public sealed class StubOrchestrator : IOrchestrator
{
    // Smallest valid PNG: a 1x1 transparent pixel. Deterministic placeholder media.
    private static readonly byte[] _onePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private const string MediaMimeType = "image/png";

    private readonly IStorageService _storage;
    private readonly IMetaIntegration _meta;
    private readonly ITrace _trace;

    public StubOrchestrator(IStorageService storage, IMetaIntegration meta, ITrace trace)
    {
        _storage = storage;
        _meta = meta;
        _trace = trace;
    }

    public async Task<RunState> RunGenerationAsync(
        RunState state,
        CancellationToken cancellationToken = default)
    {
        var trace = state.Trace;

        var strategy = new ContentStrategy(
            Pillar: "stub-pillar",
            Angle: "stub-angle",
            Objective: "stub-objective",
            Audience: "stub-audience",
            CalendarSlot: null);
        trace = await RecordAsync(trace, state, "strategy", null, cancellationToken).ConfigureAwait(false);

        var creative = new CreativeDirection(
            VisualConcept: "stub-concept",
            StyleTokens: ["soft"],
            ColorTokens: ["#ffffff"],
            MediaPromptBrief: "stub-brief");
        trace = await RecordAsync(trace, state, "creative", null, cancellationToken).ConfigureAwait(false);

        var caption = new Caption(
            Hook: "stub-hook",
            Body: "stub-body",
            Hashtags: ["#stub"]);
        trace = await RecordAsync(trace, state, "copywriting", null, cancellationToken).ConfigureAwait(false);

        // Asset id is deterministic per run so a retried segment overwrites the same
        // MinIO key (idempotent side effect, DL-022).
        var assetId = DeterministicGuid.From(state.RunId, "asset");
        var key = StorageKeys.ForAsset(state.BrandId, assetId, "png");

        MediaAssetRef? media = null;
        var errors = state.Errors;

        var mediaStartedAt = DateTimeOffset.UtcNow;
        string mediaStatus;
        string? mediaError = null;
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
            mediaStatus = "ok";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Degrade, don't crash: record a structured tool error and continue to the
            // gate with a caption-only draft. The supervisor adjudicates downstream.
            errors = [.. state.Errors, new ToolError(
                Code: "storage.put_failed",
                Message: ex.Message,
                Retryable: true)];
            mediaStatus = "error";
            mediaError = ex.Message;
        }

        trace = await _trace.RecordAsync(
            trace, state.RunId, state.BrandId, "media", "minio.put",
            mediaStatus, mediaStartedAt, DateTimeOffset.UtcNow, mediaError, cancellationToken)
            .ConfigureAwait(false);

        var draft = new ContentItemDraft(
            CaptionRef: caption,
            MediaRef: media,
            BrandId: state.BrandId,
            Status: media is null ? "degraded-caption-only" : "pending");

        return state with
        {
            Phase = GraphPhase.AwaitingApproval,
            Strategy = strategy,
            Creative = creative,
            Caption = caption,
            Media = media,
            Draft = draft,
            Errors = errors,
            Trace = trace,
        };
    }

    public async Task<RunState> RunPublishAsync(
        RunState state,
        CancellationToken cancellationToken = default)
    {
        // ContentItemId is keyed to the run so a retried publish re-uses the same
        // external reference rather than creating a second post (DL-022).
        var caption = state.Caption is null
            ? string.Empty
            : $"{state.Caption.Hook}\n\n{state.Caption.Body}";

        var request = new PublishRequest(
            BrandId: state.BrandId,
            ContentItemId: state.RunId,
            Caption: caption,
            MediaStorageKey: state.Media?.StorageKey);

        PublishResult result;
        var errors = state.Errors;

        var startedAt = DateTimeOffset.UtcNow;
        string spanStatus;
        string? spanError = null;
        try
        {
            result = await _meta.PublishAsync(request, cancellationToken).ConfigureAwait(false);
            spanStatus = "ok";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors = [.. state.Errors, new ToolError(
                Code: "meta.publish_failed",
                Message: ex.Message,
                Retryable: true)];
            result = new PublishResult(ExternalRef: null, Status: "failed", Error: ex.Message);
            spanStatus = "error";
            spanError = ex.Message;
        }

        var trace = await _trace.RecordAsync(
            state.Trace, state.RunId, state.BrandId, "publishing", "meta.publish",
            spanStatus, startedAt, DateTimeOffset.UtcNow, spanError, cancellationToken)
            .ConfigureAwait(false);

        return state with
        {
            Phase = GraphPhase.Done,
            Publish = result,
            Errors = errors,
            Trace = trace,
        };
    }

    /// <summary>Records a zero-duration span for a deterministic planning node.</summary>
    private Task<TraceRefs> RecordAsync(
        TraceRefs trace,
        RunState state,
        string node,
        string? tool,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return _trace.RecordAsync(
            trace, state.RunId, state.BrandId, node, tool, "ok", now, now, null, cancellationToken);
    }
}
