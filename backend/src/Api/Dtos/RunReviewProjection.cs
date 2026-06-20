using Backend.Core.Domain;
using Backend.Core.Integrations;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;

namespace Backend.Api.Dtos;

/// <summary>
/// Pure mapper from the persisted run state (the <see cref="AgentRun"/>, the deserialized
/// <see cref="RunState"/> checkpoint, the <see cref="ApprovalAction"/> history, and the
/// <see cref="PublishRecord"/> outcome) to the <see cref="RunReviewDto"/> the dashboard renders. No I/O
/// and no policy of its own — the available-actions list comes from <see cref="GateActionPolicy"/> (the
/// same guard the gate endpoints enforce), and the effective content applies the human edit overlay by
/// field-presence exactly as <c>PublishingExecutor</c> does (DL-035). This keeps the review surface
/// honest and consistent with what publish actually sends.
/// </summary>
public static class RunReviewProjection
{
    public static RunReviewDto From(
        AgentRun run,
        RunState? state,
        IReadOnlyList<ApprovalAction> actions,
        PublishRecord? publish,
        int maxRegenerate)
    {
        var regenerateCount = actions.Count(a => a.Action == ApprovalActionType.Regenerate);
        var available = GateActionPolicy.Available(run.Status, regenerateCount, maxRegenerate);
        var regenerate = available.Contains(GateAction.Regenerate)
            ? new RegenerateAvailabilityDto(
                GateActionPolicy.RegenerateRemaining(regenerateCount, maxRegenerate), RegenerateModes.All)
            : null;

        // Effective content = the AI draft with the latest approving edit overlaid by field-presence
        // (DL-035), mirroring PublishingExecutor. At AwaitingApproval there is no approving row yet, so
        // this shows the untouched draft; once approved it shows exactly what was published.
        var approving = actions
            .Where(a => a.Action is ApprovalActionType.Approve
                or ApprovalActionType.ApproveWithEdit
                or ApprovalActionType.ApproveWithSchedule)
            .OrderByDescending(a => a.OccurredAt)
            .FirstOrDefault();

        var (draftCaption, draftHashtags) = DraftContent(state);
        var caption = approving?.EditedCaption ?? draftCaption;
        IReadOnlyList<string> hashtags = approving?.EditedHashtags ?? draftHashtags;

        var media = state?.Draft?.MediaRef ?? state?.Media;
        // Caption-only (no media ref). The DTO field stays named BudgetDegraded for back-compat, but a
        // caption-only draft has TWO distinct causes (DL-058) and the gate must say which HONESTLY: the
        // pre-Media budget skip (no tool call) OR a media-generation FAILURE — e.g. a Veo timeout. An image
        // failure is fatal (no draft), so a media.generation_failed on a caption-only draft is always the
        // video path. The Media node records both (BudgetDegraded vs VideoDegraded); we read the typed error.
        var captionOnly = state?.Draft is { MediaRef: null };
        var mediaError = state?.Errors.FirstOrDefault(e => e.Code == "media.generation_failed");
        var degradedReason = captionOnly
            ? mediaError is not null
                ? $"Video generation failed, so the post was published caption-only: {mediaError.Message}"
                : "Media generation was skipped to stay within the run's cost ceiling (caption-only, DL-029)."
            : null;
        var imageUrl = media is not null ? $"runs/{run.Id}/media" : null;

        var grounding = state?.Strategy?.Grounding is { } g
            ? new GroundingDto(g.Grounded, g.ChunkIdsUsed, g.Confidence)
            : null;

        var candidates = state?.Candidates?.Candidates ?? [];
        var selectedAngle = state?.Strategy is { } strategy
            ? new SelectedAngleDto(
                StrategySelection.IndexOf(candidates, strategy),
                strategy.Pillar,
                strategy.Objective.ToString(),
                strategy.Angle,
                strategy.AngleRationale)
            : null;

        var alternatives = candidates
            .Select((c, i) => new AngleSummaryDto(i, c.Pillar, c.Objective.ToString(), c.Angle))
            .ToList();

        var scheduledFor = actions
            .Where(a => a.ScheduledFor is not null)
            .OrderByDescending(a => a.OccurredAt)
            .Select(a => a.ScheduledFor)
            .FirstOrDefault();

        var traceUrl = string.IsNullOrEmpty(state?.Trace.TraceId) ? null : $"runs/{run.Id}/trace";

        var budget = state?.Budget is { } b
            ? new BudgetDto(b.TokensSpent, b.TokenBudget, b.MediaSpent, b.MediaBudget)
            : new BudgetDto(0, 0, 0m, 0m);

        return new RunReviewDto(
            run.Id,
            run.Status,
            Surface(state?.TargetSurface),
            run.Modality,
            run.VideoSource,
            imageUrl,
            caption,
            hashtags,
            grounding,
            captionOnly,
            degradedReason,
            budget,
            selectedAngle,
            alternatives,
            scheduledFor,
            traceUrl,
            available,
            regenerate,
            Timeline(state, actions, publish));
    }

    private static (string Caption, IReadOnlyList<string> Hashtags) DraftContent(RunState? state)
    {
        if (state?.Draft is { } draft)
        {
            return (Compose(draft.CaptionRef.Hook, draft.CaptionRef.Body), draft.CaptionRef.Hashtags);
        }

        if (state?.Caption is { } caption)
        {
            return (Compose(caption.Hook, caption.Body), caption.Hashtags);
        }

        return (string.Empty, []);
    }

    private static List<TimelineEntryDto> Timeline(
        RunState? state, IReadOnlyList<ApprovalAction> actions, PublishRecord? publish)
    {
        var entries = actions
            .Select(a => new TimelineEntryDto(a.OccurredAt, "action", a.Action.ToString(), ActionDetail(a)))
            .ToList();

        if (publish is not null)
        {
            var detail = publish.Status == PublishStatus.Published
                ? publish.ExternalRef
                : state?.Publish?.Error ?? "Publish failed.";
            entries.Add(new TimelineEntryDto(publish.OccurredAt, "publish", publish.Status.ToString(), detail));
        }

        return entries.OrderBy(e => e.OccurredAt).ToList();
    }

    private static string? ActionDetail(ApprovalAction action) => action.Action switch
    {
        ApprovalActionType.Reject => action.Reason,
        ApprovalActionType.Regenerate => action.RegenerateMode is { } mode && action.Reason is { } reason
            ? $"{mode}: {reason}"
            : action.RegenerateMode ?? action.Reason,
        ApprovalActionType.ApproveWithEdit => "edited caption/hashtags",
        ApprovalActionType.ApproveWithSchedule => action.ScheduledFor?.ToString("u"),
        _ => null,
    };

    private static string Compose(string hook, string body) => $"{hook}\n\n{body}";

    // Mirrors PublishingExecutor.MapSurface (the surface key → container surface mapping, DL-030/038).
    private static PostSurface Surface(string? targetSurface) => targetSurface switch
    {
        "instagram_reel" => PostSurface.Reel,
        "instagram_story" => PostSurface.Story,
        _ => PostSurface.FeedImage,
    };
}
