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
/// Media Generation (DL-019/029) — a Gemini executor with no Claude prompt, forking in parallel with
/// Copywriting. Its FIRST action is the deterministic budget gate over the Supervisor's fork-time
/// snapshot (Σ pre-fork <see cref="RunState.IncurredCosts"/>, R2): (a) global-$ ceiling exceeded →
/// <see cref="RunState.FatalError"/>, zero tool calls; (b) media unaffordable → <b>media-skipped</b>
/// (null asset + a BudgetDegraded trace event, zero tool calls — caption-only, R1); (c) else render
/// the brief via <see cref="IMediaGenerationTool"/> → idempotent <see cref="IStorageService"/> write
/// (key = <c>DeterministicGuid.From(runId,"asset")</c>, DL-022). Gemini/storage failure after bounded
/// retries → <see cref="RunState.FatalError"/> (retry-then-fail-item, DL-023).
/// </summary>
public sealed class MediaGenerationExecutor : Executor<RunState, RunState>
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private const string Modality = "image";
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

        // (b) media unaffordable → media-skipped (caption-only), zero tool calls — NOT fatal (R1).
        var mediaBudget = new MediaBudget(state.Budget.MediaBudget, mediaSpent);
        if (!BudgetEvaluation.CanAffordMedia(mediaBudget, _deps.Prices.GeminiPerImage, imageCount: 1))
        {
            var detail = JsonSerializer.Serialize(
                new { @event = "BudgetDegraded", reason = "media budget exhausted", perImage = _deps.Prices.GeminiPerImage }, _json);
            var trace = await _deps.Trace.RecordAsync(
                state.Trace, state.RunId, state.BrandId, "media", null, "degraded",
                startedAt, DateTimeOffset.UtcNow, null, detail, cancellationToken).ConfigureAwait(false);
            return state with { Media = null, Trace = trace };
        }

        // (c) affordable → render the brief and store it (idempotent by deterministic asset id, DL-022).
        var assetId = DeterministicGuid.From(state.RunId, "asset");
        MediaResult? generated = null;
        ToolError? failure = null;
        var generateStartedAt = DateTimeOffset.UtcNow;
        for (var attempt = 1; attempt <= MaxGenerateAttempts; attempt++)
        {
            try
            {
                generated = await _deps.Media
                    .GenerateAsync(state.Creative.MediaPromptBrief, Modality, cancellationToken)
                    .ConfigureAwait(false);
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
            var fatal = failure
                ?? new ToolError("media.generation_failed", "media generation produced no result", Retryable: false);
            var trace = await _deps.Trace.RecordAsync(
                state.Trace, state.RunId, state.BrandId, "media", "gemini.generate", "error",
                generateStartedAt, DateTimeOffset.UtcNow, fatal.Message, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return state with { FatalError = fatal, Errors = [.. state.Errors, fatal], Trace = trace };
        }

        var generateTrace = await _deps.Trace.RecordAsync(
            state.Trace, state.RunId, state.BrandId, "media", "gemini.generate", "ok",
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

        var media = new MediaAssetRef(assetId, storedKey, Modality, generated.MimeType);
        var cost = NodeCostEstimator.ForMedia("media", _deps.Prices.GeminiPerImage);
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
