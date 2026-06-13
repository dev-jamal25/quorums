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
/// <see cref="IMetaIntegration"/>. A storage or publish failure surfaces as a
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

    public StubOrchestrator(IStorageService storage, IMetaIntegration meta)
    {
        _storage = storage;
        _meta = meta;
    }

    public async Task<RunState> RunGenerationAsync(
        RunState state,
        CancellationToken cancellationToken = default)
    {
        var strategy = new ContentStrategy(
            Pillar: "stub-pillar",
            Angle: "stub-angle",
            Objective: "stub-objective",
            Audience: "stub-audience",
            CalendarSlot: null);

        var creative = new CreativeDirection(
            VisualConcept: "stub-concept",
            StyleTokens: ["soft"],
            ColorTokens: ["#ffffff"],
            MediaPromptBrief: "stub-brief");

        var caption = new Caption(
            Hook: "stub-hook",
            Body: "stub-body",
            Hashtags: ["#stub"]);

        // Asset id is deterministic per run so a retried segment overwrites the same
        // MinIO key (idempotent side effect, DL-022).
        var assetId = DeterministicGuid.From(state.RunId, "asset");
        var key = StorageKeys.ForAsset(state.BrandId, assetId, "png");

        MediaAssetRef? media = null;
        var errors = state.Errors;

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
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Degrade, don't crash: record a structured tool error and continue to the
            // gate with a caption-only draft. The supervisor adjudicates downstream.
            errors = [.. state.Errors, new ToolError(
                Code: "storage.put_failed",
                Message: ex.Message,
                Retryable: true)];
        }

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

        try
        {
            result = await _meta.PublishAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors = [.. state.Errors, new ToolError(
                Code: "meta.publish_failed",
                Message: ex.Message,
                Retryable: true)];
            result = new PublishResult(ExternalRef: null, Status: "failed", Error: ex.Message);
        }

        return state with
        {
            Phase = GraphPhase.Done,
            Publish = result,
            Errors = errors,
        };
    }
}
