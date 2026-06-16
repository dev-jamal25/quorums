using Backend.Core.Generation.Validation;
using Backend.Core.Orchestration.Contracts;
using Xunit;

namespace Backend.UnitTests.Generation;

/// <summary>
/// The load-bearing post-deserialization field validators (DL-034 R5/R6): pillar membership,
/// selection-index range, and grounding honesty. Pure functions — these run after the forced tool
/// (which does not cover them).
/// </summary>
public sealed class FieldValidatorTests
{
    private static readonly string[] _pillars = ["Origin", "Craft", "Ritual"];

    [Theory]
    [InlineData("Origin")]
    [InlineData("origin")]      // case-insensitive
    [InlineData("  Craft  ")]   // trimmed
    public void Pillar_in_list_passes(string pillar)
    {
        Assert.Equal(PillarStatus.InList, PillarValidator.Check(pillar, _pillars));
    }

    [Fact]
    public void Pillar_outside_list_is_not_in_list()
    {
        Assert.Equal(PillarStatus.NotInList, PillarValidator.Check("Sustainability", _pillars));
        Assert.Contains("Origin, Craft, Ritual", PillarValidator.DescribeMiss("Sustainability", _pillars), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    public void Absent_pillar_list_is_an_explicit_skip_not_a_silent_pass(IReadOnlyList<string>? pillars)
    {
        Assert.Equal(PillarStatus.NoPillarsDefined, PillarValidator.Check("anything", pillars));
        Assert.Equal(PillarStatus.NoPillarsDefined, PillarValidator.Check("anything", []));
    }

    [Theory]
    [InlineData(0, 3, true)]
    [InlineData(2, 3, true)]
    [InlineData(3, 3, false)]   // == N is out of [0, N)
    [InlineData(-1, 3, false)]
    [InlineData(0, 0, false)]   // no candidates
    public void Selection_index_must_be_in_range(int chosenIndex, int candidateCount, bool expectedValid)
    {
        Assert.Equal(expectedValid, SelectionValidator.Validate(chosenIndex, candidateCount).IsValid);
    }

    [Fact]
    public void Grounding_keeps_only_the_injected_intersection_and_derives_grounded()
    {
        // Claimed grounded=false but two ids, one of which was injected → grounded becomes TRUE.
        var claimed = new Grounding(Grounded: false, ChunkIdsUsed: ["a", "b"], Confidence.High);

        var reconciled = GroundingValidator.Reconcile(claimed, injectedProvenanceIds: ["a", "z"]);

        Assert.True(reconciled.Grounded);
        Assert.Equal(["a"], reconciled.ChunkIdsUsed);
        Assert.Equal(Confidence.High, reconciled.Confidence); // confidence stays model-set
    }

    [Fact]
    public void Grounding_does_not_trust_the_self_reported_grounded_boolean()
    {
        // Claimed grounded=true but NONE of the ids were injected → grounded becomes FALSE.
        var claimed = new Grounding(Grounded: true, ChunkIdsUsed: ["x", "y"], Confidence.High);

        var reconciled = GroundingValidator.Reconcile(claimed, injectedProvenanceIds: ["a", "b"]);

        Assert.False(reconciled.Grounded);
        Assert.Empty(reconciled.ChunkIdsUsed);
    }

    [Fact]
    public void Grounding_dedupes_the_kept_intersection_and_empty_injection_is_ungrounded()
    {
        var claimed = new Grounding(true, ["a", "a", "b"], Confidence.Medium);

        Assert.Equal(["a", "b"], GroundingValidator.Reconcile(claimed, ["a", "b"]).ChunkIdsUsed);
        Assert.False(GroundingValidator.Reconcile(claimed, []).Grounded);
    }
}
