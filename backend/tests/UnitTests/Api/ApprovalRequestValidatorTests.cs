using Backend.Api.Dtos;
using Backend.Core.Generation.PlatformConstraints;
using Xunit;

namespace Backend.UnitTests.Api;

/// <summary>
/// Boundary validation of the gate decision (DL-035, DL-041, proof #1). An over-limit edit is
/// rejected at the API boundary — the <c>[ApiController]</c> auto-400 then short-circuits before the
/// action runs, so no <c>ApprovalAction</c> is written and no transition occurs. Reuses the same
/// <see cref="PlatformConstraintValidator"/> limits applied at publish (DL-030).
/// </summary>
public sealed class ApprovalRequestValidatorTests
{
    private static readonly ApprovalRequestValidator _validator = new(
        new PlatformConstraintSet([new SurfaceConstraints("instagram_feed", MaxHashtags: 30, MaxCaptionLength: 2200, AllowedAspectRatios: ["4:5", "1:1"])]));

    [Fact]
    public void Approve_with_over_limit_caption_is_invalid()
    {
        var request = new ApprovalRequest(
            GateDecision.Approve,
            new ApprovalEdits(new string('x', 2201), null),
            ScheduledFor: null,
            Reason: null);

        Assert.False(_validator.Validate(request).IsValid);
    }

    [Fact]
    public void Approve_with_over_limit_hashtags_is_invalid()
    {
        var hashtags = Enumerable.Range(0, 31).Select(i => $"#t{i}").ToList();
        var request = new ApprovalRequest(
            GateDecision.Approve, new ApprovalEdits("ok", hashtags), ScheduledFor: null, Reason: null);

        Assert.False(_validator.Validate(request).IsValid);
    }

    [Fact]
    public void Approve_with_in_limit_edit_is_valid()
    {
        var request = new ApprovalRequest(
            GateDecision.Approve, new ApprovalEdits("a tidy caption", ["#one", "#two"]), ScheduledFor: null, Reason: null);

        Assert.True(_validator.Validate(request).IsValid);
    }

    [Fact]
    public void Approve_with_past_schedule_is_invalid()
    {
        var request = new ApprovalRequest(
            GateDecision.Approve, Edits: null, ScheduledFor: DateTimeOffset.UtcNow.AddMinutes(-1), Reason: null);

        Assert.False(_validator.Validate(request).IsValid);
    }

    [Fact]
    public void Approve_with_future_schedule_is_valid()
    {
        var request = new ApprovalRequest(
            GateDecision.Approve, Edits: null, ScheduledFor: DateTimeOffset.UtcNow.AddHours(1), Reason: null);

        Assert.True(_validator.Validate(request).IsValid);
    }

    [Fact]
    public void Reject_ignores_edit_limits()
    {
        // A reject carries no edits; the edit rules must not fire.
        var request = new ApprovalRequest(GateDecision.Reject, Edits: null, ScheduledFor: null, Reason: "off-brand");

        Assert.True(_validator.Validate(request).IsValid);
    }

    [Fact]
    public void Regenerate_without_mode_is_invalid()
    {
        var request = new ApprovalRequest(GateDecision.Regenerate, Edits: null, ScheduledFor: null, Reason: "meh", Mode: null);

        Assert.False(_validator.Validate(request).IsValid);
    }

    [Fact]
    public void Regenerate_with_unknown_mode_is_invalid()
    {
        var request = new ApprovalRequest(GateDecision.Regenerate, Edits: null, ScheduledFor: null, Reason: null, Mode: "sideways");

        Assert.False(_validator.Validate(request).IsValid);
    }

    [Theory]
    [InlineData(RegenerateModes.SameAngle)]
    [InlineData(RegenerateModes.ReselectAngle)]
    public void Regenerate_with_valid_mode_is_valid(string mode)
    {
        var request = new ApprovalRequest(GateDecision.Regenerate, Edits: null, ScheduledFor: null, Reason: null, Mode: mode);

        Assert.True(_validator.Validate(request).IsValid);
    }
}
