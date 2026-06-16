using Backend.Core.Domain;
using Backend.Core.Integrations;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Microsoft.Agents.AI.Workflows;

namespace Backend.Infrastructure.Orchestration.Maf.Nodes;

/// <summary>
/// Publishing node (DL-019): translates the approved <see cref="ContentItemDraft"/> into a Meta
/// publish action via <see cref="IMetaIntegration"/> (mock in CI). The sole node of the
/// resume-segment graph. The publish is keyed by the run id, so a retried <c>ResumeRun</c>
/// re-uses the same external reference instead of creating a second post (DL-022). A publish
/// failure degrades to a structured <see cref="ToolError"/> rather than throwing into the graph.
/// <para><b>Transitional (Slice 2):</b> this node composes the two-step <see cref="IMetaIntegration"/>
/// inline with a placeholder access token and NO <c>PublishRecord</c> persistence — idempotency here
/// rests solely on the mock's stable, content-keyed external ref. Slice 4 rewires it to delegate to
/// <c>PublishCoordinator</c> (durable CreationId idempotency, the edit overlay, publish-time
/// re-check, the Vault-decrypted token, and ToolError/Failed mapping).</para>
/// </summary>
public sealed class PublishingExecutor : Executor<RunState, RunState>
{
    private readonly IMetaIntegration _meta;
    private readonly ITrace _trace;

    public PublishingExecutor(IMetaIntegration meta, ITrace trace)
        : base("publishing")
    {
        _meta = meta;
        _trace = trace;
    }

    public override ValueTask<RunState> HandleAsync(
        RunState message, IWorkflowContext context, CancellationToken cancellationToken = default)
        => new(RunAsync(message, cancellationToken));

    public async Task<RunState> RunAsync(RunState state, CancellationToken cancellationToken = default)
    {
        // ContentItemId is keyed to the run so a retried publish re-uses the same external
        // reference rather than creating a second post (DL-022).
        var caption = state.Caption is null
            ? string.Empty
            : $"{state.Caption.Hook}\n\n{state.Caption.Body}";

        var request = new PublishRequest(
            ContentItemId: state.RunId,
            Surface: MapSurface(state.TargetSurface),
            MediaUrl: state.Media?.StorageKey ?? string.Empty,
            Caption: caption,
            Hashtags: state.Caption?.Hashtags ?? [],
            AccessToken: string.Empty); // Slice 4 supplies the Vault-decrypted per-brand token.

        PublishResult result;
        var errors = state.Errors;

        var startedAt = DateTimeOffset.UtcNow;
        string status;
        string? error = null;
        try
        {
            result = await PublishTwoStepAsync(request, cancellationToken).ConfigureAwait(false);
            status = result.Status == PublishStatus.Published ? "ok" : "error";
            error = result.Error;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors = [.. state.Errors, new ToolError(
                Code: "meta.publish_failed",
                Message: ex.Message,
                Retryable: true)];
            result = new PublishResult(PublishStatus.TerminalFailure, ExternalRef: null, Error: ex.Message, EngagementKeys: null);
            status = "error";
            error = ex.Message;
        }

        var trace = await _trace.RecordAsync(
            state.Trace, state.RunId, state.BrandId, "publishing", "meta.publish",
            status, startedAt, DateTimeOffset.UtcNow, error, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return state with
        {
            Phase = GraphPhase.Done,
            Publish = result,
            Errors = errors,
            Trace = trace,
        };
    }

    // Transitional inline compose of the two-step publish (create -> poll -> publish). No
    // PublishRecord persistence; Slice 4 replaces this with PublishCoordinator delegation.
    private async Task<PublishResult> PublishTwoStepAsync(PublishRequest request, CancellationToken cancellationToken)
    {
        var created = await _meta.CreateContainerAsync(request, cancellationToken).ConfigureAwait(false);
        if (created.CreationId is not { } creationId)
        {
            return new PublishResult(created.Failure ?? PublishStatus.TerminalFailure, null, created.Error, null);
        }

        var pollResult = await _meta.PollContainerAsync(creationId, cancellationToken).ConfigureAwait(false);
        if (!pollResult.Processed)
        {
            return new PublishResult(pollResult.Failure ?? PublishStatus.TransientFailure, null, pollResult.Error, null);
        }

        return await _meta.PublishContainerAsync(creationId, cancellationToken).ConfigureAwait(false);
    }

    private static PostSurface MapSurface(string targetSurface) => targetSurface switch
    {
        "instagram_reel" => PostSurface.Reel,
        "instagram_story" => PostSurface.Story,
        _ => PostSurface.FeedImage,
    };
}
