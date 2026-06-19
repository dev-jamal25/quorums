using Backend.Core.Domain;
using Backend.Core.Generation;
using Backend.Core.Generation.PlatformConstraints;
using Backend.Core.Integrations;
using Backend.Core.Multitenancy;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Backend.Core.Secrets;
using Backend.Infrastructure.Integrations.Meta;
using Backend.Infrastructure.Persistence;
using Microsoft.Agents.AI.Workflows;
using Microsoft.EntityFrameworkCore;

namespace Backend.Infrastructure.Orchestration.Maf.Nodes;

/// <summary>
/// Publishing node (DL-019). Resolves the EFFECTIVE content (the human edit overlay), re-checks
/// PlatformConstraints, resolves the per-brand Meta token, and delegates the publish to
/// <see cref="PublishCoordinator"/> (robust CreationId idempotency, DL-039). The node records the
/// classified <see cref="PublishResult"/> on <see cref="RunState.Publish"/> and NEVER throws for a
/// classified failure — <c>ResumeRun</c> is the single point that maps the result to Done/Failed/retry
/// (mirrors the generation segment's <c>FatalError</c> marker).
/// <para>The overlay is resolved by FIELD-PRESENCE (DL-035): an edited caption/hashtag list on the
/// approving <see cref="ApprovalAction"/> wins over <see cref="RunState.Draft"/> — keyed off the
/// edited fields, NEVER off <c>Action == ApproveWithEdit</c> (an <c>ApproveWithSchedule</c> row can
/// carry edits too). <see cref="RunState.Draft"/> stays the untouched AI output.</para>
/// </summary>
public sealed class PublishingExecutor : Executor<RunState, RunState>
{
    private readonly PublishCoordinator _coordinator;
    private readonly AppDbContext _db;
    private readonly IBrandScope _scope;
    private readonly PlatformConstraintSet _constraints;
    private readonly ISecretsProvider _secrets;
    private readonly ITrace _trace;
    private readonly string _publicBaseUrl;

    public PublishingExecutor(
        PublishCoordinator coordinator,
        AppDbContext db,
        IBrandScope scope,
        PlatformConstraintSet constraints,
        ISecretsProvider secrets,
        ITrace trace,
        string publicBaseUrl)
        : base("publishing")
    {
        _coordinator = coordinator;
        _db = db;
        _scope = scope;
        _constraints = constraints;
        _secrets = secrets;
        _trace = trace;
        _publicBaseUrl = publicBaseUrl;
    }

    public override ValueTask<RunState> HandleAsync(
        RunState message, IWorkflowContext context, CancellationToken cancellationToken = default)
        => new(RunAsync(message, cancellationToken));

    public async Task<RunState> RunAsync(RunState state, CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;

        // 1) Read the approving action (overlay) + the brand Meta connection (token) under RLS.
        ApprovalAction? approving;
        BrandMetaConnection? connection;
        await using (var handle = await _scope.BeginAsync(cancellationToken).ConfigureAwait(false))
        {
            approving = await _db.ApprovalActions.AsNoTracking()
                .Where(a => a.AgentRunId == state.RunId
                    && (a.Action == ApprovalActionType.Approve
                        || a.Action == ApprovalActionType.ApproveWithEdit
                        || a.Action == ApprovalActionType.ApproveWithSchedule))
                .OrderByDescending(a => a.OccurredAt)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            connection = await _db.BrandMetaConnections.AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            await handle.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        // 2) Resolve the effective content by FIELD-PRESENCE (DL-035), falling back to the draft.
        var draftCaption = state.Draft is { } draft
            ? Compose(draft.CaptionRef.Hook, draft.CaptionRef.Body)
            : state.Caption is { } caption ? Compose(caption.Hook, caption.Body) : string.Empty;
        IReadOnlyList<string> draftHashtags = state.Draft?.CaptionRef.Hashtags ?? state.Caption?.Hashtags ?? [];

        var effectiveCaption = approving?.EditedCaption ?? draftCaption;
        IReadOnlyList<string> effectiveHashtags = approving?.EditedHashtags ?? draftHashtags;

        // 3) Per-brand Meta token (decrypt-on-use, DL-011). Absent/undecryptable → terminal, no publish.
        if (connection is null)
        {
            return await FinishAsync(
                state, startedAt, Terminal("No Meta connection is provisioned for this brand."),
                "meta.no_connection", cancellationToken).ConfigureAwait(false);
        }

        string accessToken;
        try
        {
            accessToken = await _secrets.DecryptAsync(connection.TokenCiphertext, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await FinishAsync(
                state, startedAt, Terminal($"Meta token decrypt failed: {ex.Message}"),
                "meta.token_decrypt_failed", cancellationToken).ConfigureAwait(false);
        }

        // 4) Publish-time PlatformConstraints re-check on the EFFECTIVE content (DL-030 backstop).
        if (!_constraints.TryGet(state.TargetSurface, out var surfaceConstraints))
        {
            return await FinishAsync(
                state, startedAt, Terminal($"No PlatformConstraints configured for surface '{state.TargetSurface}'."),
                "publish.surface_unconfigured", cancellationToken).ConfigureAwait(false);
        }

        // Hashtags live INSIDE the caption on IG/FB — neither channel has a separate hashtags param
        // (DL-055) — so compose them into the wire caption. Re-check the caption alone, the hashtag
        // COUNT, AND the COMBINED length against the cap (a near-limit caption + hashtags must fail here,
        // before any Meta call, not at Meta).
        var composedCaption = CaptionComposer.Compose(effectiveCaption, effectiveHashtags);

        var captionCheck = PlatformConstraintValidator.ValidateCaptionLength(effectiveCaption, surfaceConstraints);
        var hashtagCheck = PlatformConstraintValidator.ValidateHashtags(effectiveHashtags, surfaceConstraints);
        var composedCheck = PlatformConstraintValidator.ValidateCaptionLength(composedCaption, surfaceConstraints);
        if (!captionCheck.IsValid || !hashtagCheck.IsValid || !composedCheck.IsValid)
        {
            var detail = !captionCheck.IsValid ? captionCheck.Error
                : !hashtagCheck.IsValid ? hashtagCheck.Error
                : composedCheck.Error;
            return await FinishAsync(
                state, startedAt, Terminal(detail ?? "publish-time constraint violation"),
                "publish.constraint_violation", cancellationToken).ConfigureAwait(false);
        }

        // 5) Resolve the brand's connected channels from BrandMetaConnection (DL-055): Instagram when an
        //    IG Business Account id is set, Facebook Page when a Page id is set. Each is its own
        //    (contentItemId, channel) crash-safe unit. No connected channel → terminal, no publish.
        var channels = ResolveChannels(connection);
        if (channels.Count == 0)
        {
            return await FinishAsync(
                state, startedAt,
                Terminal("No Meta channel is connected for this brand (set a Facebook Page id and/or IG Business Account id)."),
                "meta.no_channel", cancellationToken).ConfigureAwait(false);
        }

        // The Meta-reachable public URL Meta fetches server-side: {Storage:PublicBaseUrl} + the brand-
        // prefixed asset key (DL-055). Empty base (CI/mock) leaves the bare key — the mock ignores it.
        var mediaUrl = BuildMediaUrl(state.Draft?.MediaRef?.StorageKey ?? state.Media?.StorageKey ?? string.Empty);

        PublishResult? result = null;
        foreach (var (channel, targetId) in channels)
        {
            var request = new PublishRequest(
                ContentItemId: state.RunId,
                Channel: channel,
                TargetId: targetId,
                Surface: MapSurface(state.TargetSurface),
                MediaUrl: mediaUrl,
                Caption: composedCaption,        // caption + hashtags as one string — the published text
                Hashtags: effectiveHashtags,     // structured list retained for the record/observability
                AccessToken: accessToken);

            result = await _coordinator
                .PublishAsync(request, state.RunId, state.BrandId, cancellationToken)
                .ConfigureAwait(false);

            // Stop on the first channel that does not publish; ResumeRun maps it to retry/Failed.
            if (result.Status != PublishStatus.Published)
            {
                break;
            }
        }

        // 6) Record the classified result; ResumeRun maps it to Done/Failed/retry. `channels` is
        //    non-empty here (guarded above), so `result` is always assigned.
        return await FinishAsync(state, startedAt, result!, "meta.publish_failed", cancellationToken).ConfigureAwait(false);
    }

    private static PublishResult Terminal(string error) =>
        new(PublishStatus.TerminalFailure, ExternalRef: null, Error: error, EngagementKeys: null);

    private async Task<RunState> FinishAsync(
        RunState state, DateTimeOffset startedAt, PublishResult result, string toolErrorCode,
        CancellationToken cancellationToken)
    {
        var published = result.Status == PublishStatus.Published;
        var errors = published
            ? state.Errors
            : [.. state.Errors, new ToolError(
                Code: toolErrorCode,
                Message: result.Error ?? "Publish failed.",
                Retryable: result.Status == PublishStatus.TransientFailure)];

        var trace = await _trace.RecordAsync(
            state.Trace, state.RunId, state.BrandId, "publishing", "meta.publish",
            published ? "ok" : "error", startedAt, DateTimeOffset.UtcNow, result.Error, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return state with
        {
            Phase = published ? GraphPhase.Done : state.Phase,
            Publish = result,
            Errors = errors,
            Trace = trace,
        };
    }

    // The brand's connected channels and their TargetIds, derived from BrandMetaConnection (DL-055):
    // Instagram when an IG Business Account id is present, Facebook Page when a Page id is present.
    private static List<(PublishChannel Channel, string TargetId)> ResolveChannels(BrandMetaConnection connection)
    {
        var channels = new List<(PublishChannel, string)>(2);
        if (!string.IsNullOrWhiteSpace(connection.IgBusinessAccountId))
        {
            channels.Add((PublishChannel.Instagram, connection.IgBusinessAccountId));
        }

        if (!string.IsNullOrWhiteSpace(connection.FacebookPageId))
        {
            channels.Add((PublishChannel.FacebookPage, connection.FacebookPageId));
        }

        return channels;
    }

    // {Storage:PublicBaseUrl}/brands/{brand_id}/assets/{asset_id}.png (DL-055). An empty base leaves the
    // bare storage key (CI/mock, where MediaUrl is ignored).
    private string BuildMediaUrl(string storageKey) =>
        string.IsNullOrEmpty(_publicBaseUrl)
            ? storageKey
            : $"{_publicBaseUrl.TrimEnd('/')}/{storageKey.TrimStart('/')}";

    private static string Compose(string hook, string body) => $"{hook}\n\n{body}";

    private static PostSurface MapSurface(string targetSurface) => targetSurface switch
    {
        "instagram_reel" => PostSurface.Reel,
        "instagram_story" => PostSurface.Story,
        _ => PostSurface.FeedImage,
    };
}
