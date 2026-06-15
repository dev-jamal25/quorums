using Backend.Infrastructure.Knowledge;
using Xunit;

namespace Backend.UnitTests.Knowledge;

/// <summary>
/// Reciprocal Rank Fusion (DL-025) — the rerank-OFF fusion of the dense ∪ sparse arms. Rank-based,
/// k≈60: rewards cross-arm agreement and higher ranks, with no cosine-vs-ts_rank scale comparison.
/// </summary>
public sealed class RrfFusionTests
{
    [Fact]
    public void Cross_arm_agreement_outscores_a_single_arm_top_hit()
    {
        var a = Guid.NewGuid();   // #1 in BOTH arms
        var b = Guid.NewGuid();   // #1 in ONE arm only
        Guid[] dense = [a, b];
        Guid[] sparse = [a, Guid.NewGuid()];

        var scores = RrfFusion.Fuse([dense, sparse], RrfFusion.DefaultK);

        Assert.True(scores[a] > scores[b]);   // a: 1/61 + 1/62 ; b: 1/62 → a wins
    }

    [Fact]
    public void Higher_rank_outscores_lower_rank_within_a_list()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Guid[] list = [first, second];

        var scores = RrfFusion.Fuse([list], 60);

        Assert.True(scores[first] > scores[second]);   // 1/61 > 1/62
    }

    [Fact]
    public void Rank_one_with_default_k_is_one_over_sixty_one()
    {
        var id = Guid.NewGuid();
        Guid[] list = [id];

        var scores = RrfFusion.Fuse([list], RrfFusion.DefaultK);

        Assert.Equal(1.0 / 61.0, scores[id], 9);   // rank 1, k=60 → 1/(60+1)
        Assert.Equal(60.0, RrfFusion.DefaultK);
    }
}
