using Backend.Api.Dtos;
using Backend.Core.Domain;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Xunit;

namespace Backend.UnitTests.Api;

/// <summary>
/// The review projection must tell the gate the HONEST reason a draft is caption-only (DL-058): a Veo
/// generation FAILURE (e.g. a poll timeout) must NOT read as a budget skip. A caption-only draft carrying
/// a <c>media.generation_failed</c> error is a video failure (image failure is fatal → no draft); without
/// one it is the pre-Media budget skip. With media present it is not degraded at all.
/// </summary>
public sealed class RunReviewProjectionTests
{
    private static RunReviewDto Project(RunState state, Modality modality)
    {
        var run = new AgentRun
        {
            Id = state.RunId,
            BrandId = state.BrandId,
            Status = RunStatus.AwaitingApproval,
            Modality = modality,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        return RunReviewProjection.From(run, state, [], publish: null, maxRegenerate: 3);
    }

    private static RunState CaptionOnlyState(IEnumerable<ToolError> errors, string surface, string modality)
    {
        var caption = new Caption("hook", "body", ["#x"], new Grounding(false, [], Confidence.Low));
        return BaseState(surface, modality) with
        {
            Caption = caption,
            Media = null,
            Draft = new ContentItemDraft(caption, MediaRef: null, BaseState(surface, modality).BrandId, "degraded-caption-only"),
            Errors = [.. errors],
        };
    }

    private static RunState BaseState(string surface, string modality) => new(
        RunId: Guid.NewGuid(),
        BrandId: Guid.NewGuid(),
        Phase: GraphPhase.AwaitingApproval,
        Strategy: null,
        Creative: null,
        Caption: null,
        Media: null,
        Draft: null,
        Approval: null,
        Publish: null,
        Budget: new Budget(0, 0, 0m, 0m),
        Errors: [],
        Trace: new TraceRefs(string.Empty, [], []),
        TargetSurface: surface,
        ContentPillars: [],
        Candidates: null,
        IncurredCosts: [],
        FatalError: null,
        Modality: modality,
        VideoSource: VideoSource.ImageSeed);

    [Fact]
    public void Video_generation_failure_reads_as_a_failure_not_a_budget_skip()
    {
        var error = new ToolError("media.generation_failed", "Veo operation 'op-1' did not finish within 00:03:00.", Retryable: false);
        var state = CaptionOnlyState([error], "instagram_reel", "video");

        var dto = Project(state, Modality.Video);

        Assert.True(dto.BudgetDegraded); // caption-only
        Assert.NotNull(dto.BudgetDegradedReason);
        Assert.Contains("Video generation failed", dto.BudgetDegradedReason!, StringComparison.Ordinal);
        Assert.Contains("did not finish within 00:03:00", dto.BudgetDegradedReason!, StringComparison.Ordinal);
        Assert.DoesNotContain("cost ceiling", dto.BudgetDegradedReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Budget_skip_with_no_media_error_still_reads_as_a_cost_ceiling_skip()
    {
        var state = CaptionOnlyState([], "instagram_reel", "video");

        var dto = Project(state, Modality.Video);

        Assert.True(dto.BudgetDegraded);
        Assert.Contains("cost ceiling", dto.BudgetDegradedReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_with_media_is_not_degraded()
    {
        var caption = new Caption("hook", "body", ["#x"], new Grounding(false, [], Confidence.Low));
        var media = new MediaAssetRef(Guid.NewGuid(), "brands/b/assets/a.mp4", "video", "video/mp4", DurationSec: 5);
        var state = BaseState("instagram_reel", "video") with
        {
            Caption = caption,
            Media = media,
            Draft = new ContentItemDraft(caption, media, BaseState("instagram_reel", "video").BrandId, "pending"),
        };

        var dto = Project(state, Modality.Video);

        Assert.False(dto.BudgetDegraded);
        Assert.Null(dto.BudgetDegradedReason);
    }
}
