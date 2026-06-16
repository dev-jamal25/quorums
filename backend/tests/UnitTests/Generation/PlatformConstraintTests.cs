using Backend.Core.Generation.PlatformConstraints;
using Backend.Core.Orchestration.Contracts;
using Xunit;

namespace Backend.UnitTests.Generation;

/// <summary>
/// Deterministic PlatformConstraints enforcement (DL-030) with the frozen per-constraint remedies:
/// hashtag-over → repair (truncate); caption-over → regenerate then hard-truncate fallback;
/// aspectRatio → stamped from the surface (R8). Plus the shared publish-time re-check validators
/// and the surface lookup.
/// </summary>
public sealed class PlatformConstraintTests
{
    private static readonly SurfaceConstraints _feed =
        new("instagram_feed", MaxHashtags: 30, MaxCaptionLength: 2200, AllowedAspectRatios: ["4:5", "1:1"]);

    private static readonly SurfaceConstraints _reel =
        new("instagram_reel", MaxHashtags: 30, MaxCaptionLength: 2200, AllowedAspectRatios: ["9:16"]);

    [Fact]
    public void Hashtags_over_limit_are_repaired_by_truncation()
    {
        var hashtags = Enumerable.Range(0, 34).Select(i => $"#tag{i}").ToList();

        var (repaired, wasRepaired) = PlatformConstraintValidator.RepairHashtags(hashtags, _feed);

        Assert.True(wasRepaired);
        Assert.Equal(30, repaired.Count);
        Assert.Equal("#tag0", repaired[0]);
    }

    [Fact]
    public void Hashtags_within_limit_are_left_unchanged()
    {
        var hashtags = new List<string> { "#a", "#b" };

        var (repaired, wasRepaired) = PlatformConstraintValidator.RepairHashtags(hashtags, _feed);

        Assert.False(wasRepaired);
        Assert.Equal(2, repaired.Count);
        Assert.True(PlatformConstraintValidator.ValidateHashtags(hashtags, _feed).IsValid);
    }

    [Fact]
    public void Hashtag_publish_recheck_fails_when_over_limit()
    {
        var over = Enumerable.Range(0, 31).Select(i => $"#t{i}").ToList();

        Assert.False(PlatformConstraintValidator.ValidateHashtags(over, _feed).IsValid);
    }

    [Fact]
    public void Caption_over_limit_fails_validation_and_truncates_as_fallback()
    {
        var longCaption = new string('x', 2201);

        Assert.False(PlatformConstraintValidator.ValidateCaptionLength(longCaption, _feed).IsValid);

        var truncated = PlatformConstraintValidator.TruncateCaption(longCaption, _feed);
        Assert.Equal(2200, truncated.Length);
    }

    [Fact]
    public void Caption_within_limit_passes_and_is_unchanged_by_truncation()
    {
        var caption = "a short caption";

        Assert.True(PlatformConstraintValidator.ValidateCaptionLength(caption, _feed).IsValid);
        Assert.Equal(caption, PlatformConstraintValidator.TruncateCaption(caption, _feed));
    }

    [Fact]
    public void AspectRatio_is_stamped_from_the_surface_overriding_any_model_value()
    {
        var brief = new MediaPromptBrief("subject", "style", "comp", "palette", "mood", null, AspectRatio: "16:9");

        var stamped = PlatformConstraintValidator.StampAspectRatio(brief, _reel);

        Assert.Equal("9:16", stamped.AspectRatio); // canonical (first allowed) for the reel surface
        Assert.Equal("subject", stamped.Subject);  // the rest of the brief is untouched
    }

    [Fact]
    public void AspectRatio_publish_recheck_validates_against_the_allowed_set()
    {
        Assert.True(PlatformConstraintValidator.ValidateAspectRatio("4:5", _feed).IsValid);
        Assert.True(PlatformConstraintValidator.ValidateAspectRatio("1:1", _feed).IsValid);
        Assert.False(PlatformConstraintValidator.ValidateAspectRatio("9:16", _feed).IsValid);
    }

    [Fact]
    public void Constraint_set_resolves_by_surface_and_rejects_unknown_surfaces()
    {
        var set = new PlatformConstraintSet([_feed, _reel]);

        Assert.Equal(30, set.For("instagram_feed").MaxHashtags);
        Assert.True(set.TryGet("instagram_reel", out _));
        Assert.Throws<KeyNotFoundException>(() => set.For("tiktok"));
    }
}
