using Backend.Core.Domain;
using Backend.Infrastructure.Knowledge;
using Xunit;

namespace Backend.UnitTests.Knowledge;

public sealed class TypeDispatchedChunkerTests
{
    private readonly TypeDispatchedChunker _chunker = new();

    [Theory]
    [InlineData(DocType.HistoricalPost)]
    [InlineData(DocType.Product)]
    [InlineData(DocType.PlatformGuidance)]
    public void Whole_unit_types_are_never_split(DocType type)
    {
        var longText = string.Join(" ", Enumerable.Repeat("word", 2000));

        var chunks = _chunker.Chunk(type, longText);

        Assert.Single(chunks);
        Assert.Equal(longText, chunks[0].Content);
    }

    [Fact]
    public void Brand_playbook_prose_is_windowed_with_overlap()
    {
        var prose = string.Join(" ", Enumerable.Range(0, 1500).Select(i => $"w{i}"));

        var chunks = _chunker.Chunk(DocType.BrandPlaybook, prose);

        Assert.True(chunks.Count > 1);                                       // windowed
        Assert.Contains(chunks.Zip(chunks.Skip(1)), p => Overlaps(p.First.Content, p.Second.Content));
        Assert.All(chunks, c => Assert.DoesNotContain("engagement_rate", c.Content)); // text stays clean
    }

    [Fact]
    public void Market_intel_competitor_copy_is_whole_unit_but_article_is_windowed()
    {
        var prose = string.Join(" ", Enumerable.Range(0, 1500).Select(i => $"w{i}"));

        // DL-026 sub-dispatch: a competitor caption is atomic → whole-unit regardless of length.
        Assert.Single(_chunker.Chunk(DocType.MarketIntel, prose, isCompetitor: true));
        // An article is prose → section-aware window.
        Assert.True(_chunker.Chunk(DocType.MarketIntel, prose, isCompetitor: false).Count > 1);
    }

    [Fact]
    public void Empty_content_yields_no_chunks()
    {
        Assert.Empty(_chunker.Chunk(DocType.Product, "   "));
    }

    // Each synthetic word is unique, so consecutive windowed chunks share a word ONLY in
    // their overlap region — a non-empty intersection proves the sliding window overlaps.
    private static bool Overlaps(string a, string b)
    {
        var aWords = a.Split(' ').ToHashSet();
        return b.Split(' ').Any(aWords.Contains);
    }
}
