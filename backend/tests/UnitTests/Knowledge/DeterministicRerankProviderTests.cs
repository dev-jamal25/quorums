using Backend.Infrastructure.Knowledge;
using Xunit;

namespace Backend.UnitTests.Knowledge;

public sealed class DeterministicRerankProviderTests
{
    [Fact]
    public async Task Relevance_ranks_lexically_closer_doc_higher()
    {
        var p = new DeterministicRerankProvider();
        var scores = await p.RerankAsync("floral light roast",
        [
            "chocolate caramel medium-dark espresso",      // index 0 — far
            "floral jasmine bergamot light roast washed",  // index 1 — near
        ]);

        var top = scores.OrderByDescending(s => s.Relevance).First();
        Assert.Equal(1, top.Index);     // proves it reorders away from input order
        Assert.Equal(2, scores.Count);  // one score per input doc
    }
}
