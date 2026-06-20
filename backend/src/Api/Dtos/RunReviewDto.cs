using Backend.Core.Domain;
using Backend.Core.Integrations;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;

namespace Backend.Api.Dtos;

/// <summary>
/// The server-computed run-review projection (DL-041). Adapts to the run's status: at
/// <see cref="RunStatus.AwaitingApproval"/> it surfaces the current AI draft for a decision; once
/// terminal it reflects the published (overlaid) content and outcome. The reviewer sees the full
/// context — grounding/provenance, the selected angle + alternatives, the BudgetDegraded state, and
/// the audit timeline — and the list of currently-legal actions. The client renders
/// <see cref="AvailableActions"/> verbatim and NEVER recomputes gate policy.
/// </summary>
public sealed record RunReviewDto(
    Guid RunId,
    RunStatus Status,
    PostSurface Surface,
    Modality Modality,
    VideoSource? VideoSource,
    string? ImageUrl,
    string Caption,
    IReadOnlyList<string> Hashtags,
    GroundingDto? Grounding,
    bool BudgetDegraded,
    string? BudgetDegradedReason,
    BudgetDto Budget,
    SelectedAngleDto? SelectedAngle,
    IReadOnlyList<AngleSummaryDto> AlternativeAngles,
    DateTimeOffset? ScheduledFor,
    string? TraceUrl,
    IReadOnlyList<GateAction> AvailableActions,
    RegenerateAvailabilityDto? Regenerate,
    IReadOnlyList<TimelineEntryDto> Timeline);

/// <summary>Grounding/provenance summary (DL-028): the grounded flag, the provenance chunk ids, and the
/// model's self-reported confidence. Never the raw chunk text or token material (api.md).</summary>
public sealed record GroundingDto(bool Grounded, IReadOnlyList<string> ChunkIdsUsed, Confidence Confidence);

/// <summary>The chosen <see cref="ContentStrategy"/> (DL-027): its index in the banked candidates plus
/// the explainable "why this content" — pillar, objective, and the selection rationale.</summary>
public sealed record SelectedAngleDto(int ChosenIndex, string Pillar, string Objective, string Angle, string Rationale);

/// <summary>One banked alternative angle (DL-027) — what reselect-angle would choose from.</summary>
public sealed record AngleSummaryDto(int Index, string Pillar, string Objective, string Angle);

/// <summary>Budget consumed vs ceiling (DL-029), so the reviewer sees the spend behind a degrade.</summary>
public sealed record BudgetDto(int TokensSpent, int TokenBudget, decimal MediaSpent, decimal MediaBudget);

/// <summary>Regenerate availability detail (DL-036): how many remain and the modes to choose from.
/// Present only when <see cref="GateAction.Regenerate"/> is in <see cref="RunReviewDto.AvailableActions"/>.</summary>
public sealed record RegenerateAvailabilityDto(int Remaining, IReadOnlyList<string> Modes);

/// <summary>One entry in the per-post timeline (DL-040) — a human <c>ApprovalAction</c> or the system
/// <c>PublishRecord</c> outcome, merged by time into a single read projection.</summary>
public sealed record TimelineEntryDto(DateTimeOffset OccurredAt, string Kind, string Label, string? Detail);
