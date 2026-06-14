using Backend.Core.Domain;

namespace Backend.Core.Knowledge;

/// <summary>One windowed/whole chunk of a doc's raw content. Index is the chunk's
/// position within the doc (the deterministic upsert key derives from it).</summary>
public sealed record ChunkDraft(int Index, string Content);

/// <summary>
/// Splits a doc's raw content per its DocType (DL-026), dispatching to exactly two
/// primitives: whole-unit (atomic) and section-aware window. Structured metadata is NOT
/// passed in — it stays out of the chunk text. The <c>isCompetitor</c> flag drives the
/// market_intel sub-dispatch (DL-026): competitor copy is atomic → whole-unit, an article
/// is prose → section-aware window. It is ignored for every other DocType.
/// </summary>
public interface IKnowledgeChunker
{
    IReadOnlyList<ChunkDraft> Chunk(DocType docType, string rawContent, bool isCompetitor = false);
}
