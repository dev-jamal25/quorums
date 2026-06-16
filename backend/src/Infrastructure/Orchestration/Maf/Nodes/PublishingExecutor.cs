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
            BrandId: state.BrandId,
            ContentItemId: state.RunId,
            Caption: caption,
            MediaStorageKey: state.Media?.StorageKey);

        PublishResult result;
        var errors = state.Errors;

        var startedAt = DateTimeOffset.UtcNow;
        string status;
        string? error = null;
        try
        {
            result = await _meta.PublishAsync(request, cancellationToken).ConfigureAwait(false);
            status = "ok";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors = [.. state.Errors, new ToolError(
                Code: "meta.publish_failed",
                Message: ex.Message,
                Retryable: true)];
            result = new PublishResult(ExternalRef: null, Status: "failed", Error: ex.Message);
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
}
