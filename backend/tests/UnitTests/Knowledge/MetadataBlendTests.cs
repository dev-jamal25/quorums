using Backend.Core.Domain;
using Backend.Infrastructure.Configuration.Options;
using Backend.Infrastructure.Knowledge;
using Xunit;

namespace Backend.UnitTests.Knowledge;

/// <summary>
/// The S2 blend is an additive weighted sum (DL-025, JC-2): α·rel (+ β·perf + γ·segment) for
/// historical_post, α·rel + δ·recency for market_intel, α·rel otherwise. β=γ=δ=0 ⇒ pure rerank order.
/// </summary>
public sealed class MetadataBlendTests
{
    private static readonly RetrievalBlendOptions _w =
        new() { Alpha = 1.0, Beta = 0.3, Gamma = 0.0, Delta = 0.3, RecencyHalfLifeDays = 30 };

    [Fact]
    public void Historical_post_with_higher_performance_outscores_a_near_tie()
    {
        // Equal relNorm (a near tie from the reranker) ⇒ the perf term decides.
        var sHigh = MetadataBlend.Score(relNorm: 0.8, perfNorm: 1.0, segmentMatch: 0.0, recencyDecay: 0.0, DocType.HistoricalPost, _w);
        var sLow = MetadataBlend.Score(relNorm: 0.8, perfNorm: 0.0, segmentMatch: 0.0, recencyDecay: 0.0, DocType.HistoricalPost, _w);
        Assert.True(sHigh > sLow);
    }

    [Fact]
    public void Beta_zero_reproduces_pure_relevance_order()
    {
        var w0 = _w with { Beta = 0, Delta = 0, Gamma = 0 };
        Assert.Equal(0.8, MetadataBlend.Score(0.8, 1.0, 0.0, 0.0, DocType.HistoricalPost, w0));
    }

    [Fact]
    public void Market_intel_fresher_intel_outscores_stale()
    {
        var fresh = MetadataBlend.Score(0.8, 0, 0, recencyDecay: 1.0, DocType.MarketIntel, _w);
        var stale = MetadataBlend.Score(0.8, 0, 0, recencyDecay: 0.1, DocType.MarketIntel, _w);
        Assert.True(fresh > stale);
    }

    [Fact]
    public void Recency_decay_halves_each_half_life_and_is_zero_without_a_date()
    {
        var now = new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(1.0, MetadataBlend.RecencyDecay(now, now, 30), 6);
        Assert.Equal(0.5, MetadataBlend.RecencyDecay(now.AddDays(-30), now, 30), 6);
        Assert.Equal(0.0, MetadataBlend.RecencyDecay(null, now, 30));
    }
}
