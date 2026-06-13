using Backend.Core.Common;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Backend.Core.Storage;

namespace Backend.Infrastructure.Orchestration;

/// <summary>
/// Deterministic placeholder orchestrator (no LLM, no MAF). It produces fixed typed
/// outputs but performs <em>real</em> I/O on the proven durable seam: the media step
/// writes a real object to MinIO. A storage failure surfaces as a structured
/// <see cref="ToolError"/> on <see cref="RunState.Errors"/> (DL-022) rather than an
/// exception into the graph; the asset id is derived from the run id so a Hangfire
/// retry overwrites the same key instead of duplicating.
/// </summary>
public sealed class StubOrchestrator : IOrchestrator
{
    // Smallest valid PNG: a 1x1 transparent pixel. Deterministic placeholder media.
    private static readonly byte[] _onePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private const string MediaMimeType = "image/png";

    private readonly IStorageService _storage;

    public StubOrchestrator(IStorageService storage) => _storage = storage;

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

    public Task<RunState> RunPublishAsync(RunState state, CancellationToken cancellationToken = default)
    {
        var result = new PublishResult(
            ExternalRef: null,
            Status: "stub-published",
            Error: null);

        return Task.FromResult(state with
        {
            Phase = GraphPhase.Done,
            Publish = result,
        });
    }
}
