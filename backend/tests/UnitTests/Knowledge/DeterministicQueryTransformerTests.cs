using Backend.Infrastructure.Knowledge;
using Xunit;

namespace Backend.UnitTests.Knowledge;

public sealed class DeterministicQueryTransformerTests
{
    [Fact]
    public async Task Expand_returns_requested_count_deterministically()
    {
        var t = new DeterministicQueryTransformer();

        var a = await t.ExpandAsync("light roast notes", 3);
        var b = await t.ExpandAsync("light roast notes", 3);

        Assert.Equal(3, a.Count);
        Assert.Equal(a, b);                                                       // deterministic
        Assert.Contains(a, v => v.Contains("light roast notes", StringComparison.Ordinal));  // original signal preserved
    }
}
