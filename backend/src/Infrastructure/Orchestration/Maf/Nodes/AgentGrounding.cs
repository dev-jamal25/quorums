using Backend.Core.Domain;
using Backend.Core.Generation.Prompting;
using Backend.Core.Knowledge;

namespace Backend.Infrastructure.Orchestration.Maf.Nodes;

/// <summary>
/// Retrieves an agent's corpus slices via <see cref="IRetrievalService"/> (brand-scoped under the
/// job's RLS binding) and maps the hits to P1 <see cref="GroundingChunk"/>s for the prompt's
/// grounding block (DL-026/027). Empty recall or a retrieval <c>ToolError</c> degrades to fewer/no
/// chunks — never a crash (DL-022); the agent proceeds ungrounded (R8) and the grounding validator
/// derives <c>grounded</c> from the injected provenance ids.
/// </summary>
internal static class AgentGrounding
{
    /// <summary>Top-k hits retrieved per docType slice (kept small to bound the prompt).</summary>
    public const int TopKPerDocType = 3;

    public static async Task<IReadOnlyList<GroundingChunk>> RetrieveAsync(
        IRetrievalService retrieval,
        Guid brandId,
        string query,
        IReadOnlyList<DocType> docTypes)
    {
        var chunks = new List<GroundingChunk>();

        foreach (var docType in docTypes)
        {
            var result = await retrieval.Retrieve(query, brandId, docType, TopKPerDocType).ConfigureAwait(false);
            if (result.Error is not null)
            {
                continue; // degrade: drop this slice, proceed with what we have (DL-022)
            }

            chunks.AddRange(result.Chunks.Select(chunk =>
                new GroundingChunk(chunk.ChunkId.ToString(), chunk.DocType.ToString(), chunk.Content)));
        }

        return chunks;
    }
}
