using Backend.Core.Domain;
using Backend.Core.Knowledge;

namespace Backend.Infrastructure.Knowledge;

/// <summary>
/// One pipeline, two primitives, dispatched on DocType (DL-026). Whole-unit content is
/// embedded as a single chunk; prose is split on blank-line sections then sliding-windowed
/// within each section. Token count is approximated by whitespace words (no tokenizer dep).
/// </summary>
public sealed class TypeDispatchedChunker : IKnowledgeChunker
{
    private const int WindowWords = 450;   // ~600 tokens
    private const int OverlapWords = 45;   // ~60 tokens

    public IReadOnlyList<ChunkDraft> Chunk(DocType docType, string rawContent, bool isCompetitor = false)
    {
        var text = rawContent?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return [];
        }

        return docType switch
        {
            // brand_playbook prose → section-aware window.
            DocType.BrandPlaybook => Windowed(text),
            // market_intel sub-dispatch (DL-026): competitor copy is atomic → whole-unit;
            // an article is prose → section-aware window.
            DocType.MarketIntel => isCompetitor ? [new ChunkDraft(0, text)] : Windowed(text),
            // Whole-unit for atomic content (historical_post, product/FAQ, platform_guidance).
            _ => [new ChunkDraft(0, text)],
        };
    }

    private static List<ChunkDraft> Windowed(string text)
    {
        // Split on blank-line "sections" so a window never straddles unrelated headings.
        var sections = text.Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries);
        var chunks = new List<ChunkDraft>();
        var index = 0;

        foreach (var section in sections)
        {
            var words = section.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length <= WindowWords)
            {
                chunks.Add(new ChunkDraft(index++, section.Trim()));
                continue;
            }

            for (var start = 0; start < words.Length; start += WindowWords - OverlapWords)
            {
                var slice = words.Skip(start).Take(WindowWords);
                chunks.Add(new ChunkDraft(index++, string.Join(' ', slice)));
                if (start + WindowWords >= words.Length)
                {
                    break;
                }
            }
        }

        return chunks;
    }
}
