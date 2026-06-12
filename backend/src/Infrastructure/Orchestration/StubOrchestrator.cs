using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;

namespace Backend.Infrastructure.Orchestration;

public sealed class StubOrchestrator : IOrchestrator
{
    public Task<RunState> RunGenerationAsync(RunState state, CancellationToken cancellationToken = default)
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

        var media = new MediaAssetRef(
            AssetId: Guid.NewGuid(),
            StorageKey: $"brands/{state.BrandId}/assets/stub",
            Modality: "image",
            MimeType: "image/png");

        var draft = new ContentItemDraft(
            CaptionRef: caption,
            MediaRef: media,
            BrandId: state.BrandId,
            Status: "pending");

        return Task.FromResult(state with
        {
            Phase = GraphPhase.AwaitingApproval,
            Strategy = strategy,
            Creative = creative,
            Caption = caption,
            Media = media,
            Draft = draft,
        });
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
