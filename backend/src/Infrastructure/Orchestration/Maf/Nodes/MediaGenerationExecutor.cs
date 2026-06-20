using System.Text.Json;
using Backend.Core.Common;
using Backend.Core.Generation.Cost;
using Backend.Core.Integrations;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Backend.Core.Storage;
using Backend.Infrastructure.Generation;
using Microsoft.Agents.AI.Workflows;

namespace Backend.Infrastructure.Orchestration.Maf.Nodes;

/// <summary>
/// Media Generation (DL-019/029/058) — a Gemini/Veo executor with no Claude prompt, forking in parallel
/// with Copywriting. Its FIRST action is the deterministic budget gate over the Supervisor's fork-time
/// snapshot (Σ pre-fork <see cref="RunState.IncurredCosts"/>, R2): (a) global-$ ceiling exceeded →
/// <see cref="RunState.FatalError"/>, zero tool calls; (b) media unaffordable → <b>media-skipped</b>
/// (null asset + a BudgetDegraded trace event, zero tool calls — caption-only, R1; image = per-image
/// price, video = per-second price × duration + the seed image); (c) else render the brief via
/// <see cref="IMediaGenerationTool"/> → idempotent <see cref="IStorageService"/> write (key =
/// <c>DeterministicGuid.From(runId,"asset")</c>, DL-022). The video path is submit-or-resume idempotent
/// on that asset id (<c>VeoOperationStore</c>, evicted here after commit). Failure after bounded retries
/// branches on modality (DL-058): <b>image</b> → <see cref="RunState.FatalError"/> (retry-then-fail-item,
/// DL-023); <b>video</b> (Veo timeout/terminal) → caption-only degrade so the run still reaches the gate.
/// </summary>
public sealed class MediaGenerationExecutor : Executor<RunState, RunState>
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private const int MaxGenerateAttempts = 3;

    private readonly GenerationAgentDeps _deps;

    public MediaGenerationExecutor(GenerationAgentDeps deps)
        : base("media-generation") => _deps = deps;

    public override ValueTask<RunState> HandleAsync(
        RunState message, IWorkflowContext context, CancellationToken cancellationToken = default)
        => new(RunAsync(message, cancellationToken));

    public async Task<RunState> RunAsync(RunState state, CancellationToken cancellationToken = default)
    {
        if (state.FatalError is not null || state.Creative is null)
        {
            return state;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var brief = state.Creative.MediaPromptBrief;
        var isVideo = string.Equals(brief.Modality, "video", StringComparison.OrdinalIgnoreCase);
        var assetId = DeterministicGuid.From(state.RunId, "asset");

        // Asset-level idempotency (DL-058). A whole-ExecuteRun retry AFTER a prior successful generation
        // (its in-flight Veo op already evicted) would re-enter this node, miss the op store, and submit a
        // fresh PAID Veo job. So before the budget gate, the seed-image sub-call, AND the Veo submit, reuse
        // the deterministic asset if it already exists in storage. MinIO PutObject is atomic, so an existing
        // object is a complete asset — safe to reuse. Video only (the paid Veo path); image keeps its
        // overwrite-by-key behavior, and the asset is already paid so no budget/ceiling re-check applies.
        if (isVideo)
        {
            var existingKey = StorageKeys.ForAsset(state.BrandId, assetId, "mp4");
            if (await _deps.Storage.ExistsAsync(existingKey, cancellationToken).ConfigureAwait(false))
            {
                _deps.VeoStore.Remove(assetId);
                var reusedDuration = Math.Clamp(brief.DurationSec ?? _deps.MaxVideoDurationSec, 1, _deps.MaxVideoDurationSec);
                var reusedMedia = new MediaAssetRef(assetId, existingKey, brief.Modality, "video/mp4", reusedDuration);
                var reuseDetail = JsonSerializer.Serialize(new { @event = "AssetReused", key = existingKey }, _json);
                var reuseTrace = await _deps.Trace.RecordAsync(
                    state.Trace, state.RunId, state.BrandId, "media", "minio.exists", "ok",
                    startedAt, DateTimeOffset.UtcNow, null, reuseDetail, cancellationToken).ConfigureAwait(false);
                return state with { Media = reusedMedia, Trace = reuseTrace };
            }
        }

        // Fork-time snapshot = pre-fork spend only (this branch never sees the parallel Copywriting tokens).
        var snapshotUsd = state.IncurredCosts.Sum(cost => cost.TokenUsd + cost.MediaUsd);
        var mediaSpent = state.IncurredCosts.Sum(cost => cost.MediaUsd);

        // (a) global-ceiling breach → fail the run, zero tool calls (DL-029).
        if (BudgetEvaluation.ExceedsCeiling(snapshotUsd, _deps.GlobalCeilingUsd))
        {
            var fatal = new ToolError(
                Code: "budget.ceiling_exceeded",
                Message: $"fork-time spend ${snapshotUsd:0.######} exceeds the global ceiling ${_deps.GlobalCeilingUsd:0.######}",
                Retryable: false);
            var detail = JsonSerializer.Serialize(
                new { @event = "CeilingExceeded", snapshotUsd, ceiling = _deps.GlobalCeilingUsd }, _json);
            var trace = await _deps.Trace.RecordAsync(
                state.Trace, state.RunId, state.BrandId, "media", null, "error",
                startedAt, DateTimeOffset.UtcNow, fatal.Message, detail, cancellationToken).ConfigureAwait(false);
            return state with { FatalError = fatal, Errors = [.. state.Errors, fatal], Trace = trace };
        }

        // (b) media unaffordable → media-skipped (caption-only), zero tool calls — NOT fatal (R1). The
        // affordability check branches on modality (DL-058): image = per-image price; video =
        // per-second price × duration (+ the one seed image when image-seeded).
        var durationSec = brief.DurationSec ?? _deps.MaxVideoDurationSec;
        var seedsImage = state.VideoSource == VideoSource.ImageSeed;
        var mediaBudget = new MediaBudget(state.Budget.MediaBudget, mediaSpent);
        var affordable = isVideo
            ? BudgetEvaluation.CanAffordVideo(
                mediaBudget, _deps.VideoPricePerSec, durationSec, seedsImage, _deps.Prices.GeminiPerImage)
            : BudgetEvaluation.CanAffordMedia(mediaBudget, _deps.Prices.GeminiPerImage, imageCount: 1);
        if (!affordable)
        {
            var reason = isVideo ? "video budget exhausted" : "media budget exhausted";
            var detail = JsonSerializer.Serialize(
                new { @event = "BudgetDegraded", reason, modality = brief.Modality }, _json);
            var trace = await _deps.Trace.RecordAsync(
                state.Trace, state.RunId, state.BrandId, "media", null, "degraded",
                startedAt, DateTimeOffset.UtcNow, null, detail, cancellationToken).ConfigureAwait(false);
            return state with { Media = null, Trace = trace };
        }

        // (c) affordable → render the brief and store it (idempotent by deterministic asset id, DL-022).
        // The video path is submit-or-resume on this assetId, so an in-node retry resumes the in-flight
        // Veo op and bills zero new jobs (DL-058).
        var mediaRequest = new MediaGenerationRequest(brief, assetId, state.VideoSource);
        var toolName = isVideo ? "veo.generate" : "gemini.generate";
        MediaResult? generated = null;
        ToolError? failure = null;
        var generateStartedAt = DateTimeOffset.UtcNow;
        for (var attempt = 1; attempt <= MaxGenerateAttempts; attempt++)
        {
            try
            {
                generated = await _deps.Media.GenerateAsync(mediaRequest, cancellationToken).ConfigureAwait(false);
                failure = null;
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = new ToolError("media.generation_failed", ex.Message, Retryable: false);
            }
        }

        if (generated is null)
        {
            var error = failure
                ?? new ToolError("media.generation_failed", "media generation produced no result", Retryable: false);

            // DL-058: a video timeout/terminal error degrades to caption-only (the run still reaches the
            // gate); an image failure stays fatal (retry-then-fail-item, unchanged — no regression).
            if (isVideo)
            {
                var detail = JsonSerializer.Serialize(
                    new { @event = "VideoDegraded", reason = error.Message, modality = brief.Modality }, _json);
                var degradeTrace = await _deps.Trace.RecordAsync(
                    state.Trace, state.RunId, state.BrandId, "media", toolName, "degraded",
                    generateStartedAt, DateTimeOffset.UtcNow, error.Message, detail, cancellationToken)
                    .ConfigureAwait(false);
                return state with { Media = null, Errors = [.. state.Errors, error], Trace = degradeTrace };
            }

            var trace = await _deps.Trace.RecordAsync(
                state.Trace, state.RunId, state.BrandId, "media", toolName, "error",
                generateStartedAt, DateTimeOffset.UtcNow, error.Message, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return state with { FatalError = error, Errors = [.. state.Errors, error], Trace = trace };
        }

        var generateTrace = await _deps.Trace.RecordAsync(
            state.Trace, state.RunId, state.BrandId, "media", toolName, "ok",
            generateStartedAt, DateTimeOffset.UtcNow, null, cancellationToken: cancellationToken).ConfigureAwait(false);

        var extension = generated.MimeType.Split('/')[^1];
        var key = StorageKeys.ForAsset(state.BrandId, assetId, extension);
        var putStartedAt = DateTimeOffset.UtcNow;
        string storedKey;
        try
        {
            storedKey = await _deps.Storage
                .PutAsync(key, generated.Bytes, generated.MimeType, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var fatal = new ToolError("media.generation_failed", ex.Message, Retryable: false);
            var trace = await _deps.Trace.RecordAsync(
                generateTrace, state.RunId, state.BrandId, "media", "minio.put", "error",
                putStartedAt, DateTimeOffset.UtcNow, fatal.Message, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return state with { FatalError = fatal, Errors = [.. state.Errors, fatal], Trace = trace };
        }

        // Asset committed → evict the in-flight Veo op (submit-or-resume eviction is node-side, after
        // commit, so a clean retry never resumes a finished op — DL-058). No-op for image.
        if (isVideo)
        {
            _deps.VeoStore.Remove(assetId);
        }

        var media = new MediaAssetRef(assetId, storedKey, brief.Modality, generated.MimeType, generated.DurationSec);
        var cost = isVideo
            ? NodeCostEstimator.ForVideo(
                "media", _deps.VideoPricePerSec, durationSec, seedsImage, _deps.Prices.GeminiPerImage)
            : NodeCostEstimator.ForMedia("media", _deps.Prices.GeminiPerImage);
        var putTrace = await _deps.Trace.RecordAsync(
            generateTrace, state.RunId, state.BrandId, "media", "minio.put", "ok",
            putStartedAt, DateTimeOffset.UtcNow, null, cancellationToken: cancellationToken).ConfigureAwait(false);

        return state with
        {
            Media = media,
            IncurredCosts = [.. state.IncurredCosts, cost],
            Trace = putTrace,
        };
    }
}
