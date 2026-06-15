using Backend.Core.Domain;
using Backend.Infrastructure.Configuration.Options;

namespace Backend.Infrastructure.Knowledge;

/// <summary>
/// The S2 metadata blend (DL-025, JC-2). Additive weighted sum on normalized terms; the reranker
/// stays pure (<c>IRerankProvider</c>) — this is the ONLY place metadata touches the score. With
/// β=γ=δ=0 it collapses to <c>relNorm</c> (pure rerank order). The additive per-docType form is
/// skill/DL-025-specified; the normalization of the inputs is the chosen judgment call (JC-2).
/// </summary>
internal static class MetadataBlend
{
    public static double Score(
        double relNorm, double perfNorm, double segmentMatch, double recencyDecay,
        DocType docType, RetrievalBlendOptions w) => docType switch
        {
            DocType.HistoricalPost => (w.Alpha * relNorm) + (w.Beta * perfNorm) + (w.Gamma * segmentMatch),
            DocType.MarketIntel => (w.Alpha * relNorm) + (w.Delta * recencyDecay),
            _ => w.Alpha * relNorm,   // α = 1 baseline (pure relevance)
        };

    public static double RecencyDecay(DateTimeOffset? date, DateTimeOffset now, double halfLifeDays)
    {
        if (date is null || halfLifeDays <= 0)
        {
            return 0.0;
        }

        var ageDays = Math.Max(0.0, (now - date.Value).TotalDays);
        return Math.Pow(2.0, -ageDays / halfLifeDays);
    }
}
