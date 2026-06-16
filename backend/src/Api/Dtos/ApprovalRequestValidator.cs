using Backend.Core.Generation.PlatformConstraints;
using FluentValidation;

namespace Backend.Api.Dtos;

/// <summary>
/// Fail-fast boundary validation for the gate decision (DL-035, DL-041). An over-limit edit is a
/// 400 before any decision is recorded. Reuses the SAME <see cref="PlatformConstraintValidator"/>
/// checks applied at publish (DL-030) — the IG caption/hashtag limits (2200 / 30) are uniform across
/// surfaces, so the configured <c>instagram_feed</c> constraints are the canonical source.
/// </summary>
public sealed class ApprovalRequestValidator : AbstractValidator<ApprovalRequest>
{
    public ApprovalRequestValidator(PlatformConstraintSet constraints)
    {
        ArgumentNullException.ThrowIfNull(constraints);

        var ig = constraints.For("instagram_feed");

        When(r => r.Decision == GateDecision.Approve && r.Edits is not null, () =>
        {
            RuleFor(r => r.Edits!.Caption)
                .Must(caption => caption is null || PlatformConstraintValidator.ValidateCaptionLength(caption, ig).IsValid)
                .WithMessage($"Caption exceeds the {ig.MaxCaptionLength}-character limit.");

            RuleFor(r => r.Edits!.Hashtags)
                .Must(hashtags => hashtags is null || PlatformConstraintValidator.ValidateHashtags(hashtags, ig).IsValid)
                .WithMessage($"Hashtags exceed the limit of {ig.MaxHashtags}.");
        });

        RuleFor(r => r.ScheduledFor)
            .Must(scheduledFor => scheduledFor is null || scheduledFor.Value > DateTimeOffset.UtcNow)
            .WithMessage("scheduledFor must be in the future (UTC).")
            .When(r => r.Decision == GateDecision.Approve);

        // Regenerate requires a mode (DL-036). The per-run regen bound is enforced in the endpoint
        // (a DB count), not here.
        RuleFor(r => r.Mode)
            .Must(mode => mode is not null && RegenerateModes.All.Contains(mode, StringComparer.Ordinal))
            .WithMessage($"mode is required for regenerate and must be one of: {string.Join(", ", RegenerateModes.All)}.")
            .When(r => r.Decision == GateDecision.Regenerate);
    }
}
