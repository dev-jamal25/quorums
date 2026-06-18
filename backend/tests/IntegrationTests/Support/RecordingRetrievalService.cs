using Backend.Core.Domain;
using Backend.Core.Knowledge;

namespace Backend.IntegrationTests.Support;

/// <summary>
/// Read-only recording double (Option A) that wraps a real <see cref="IRetrievalService"/> and captures
/// the provenance chunk-ids it returns — the set actually injected into agent prompts. The
/// <c>SystemOutput</c> projection reads these per-node injected ids for the grounding-honesty evaluator,
/// which are <b>not</b> recoverable from RunState/trace (the executors discard them). Mirrors the existing
/// <c>Recording*</c> test doubles; no production code or frozen contract changes.
/// </summary>
internal sealed class RecordingRetrievalService : IRetrievalService
{
    private readonly IRetrievalService _inner;
    private readonly List<string> _provenanceIds = [];

    public RecordingRetrievalService(IRetrievalService inner) => _inner = inner;

    /// <summary>The distinct union of provenance ids returned across every retrieval call.</summary>
    public IReadOnlyList<string> AllProvenanceIds =>
        _provenanceIds.Distinct(StringComparer.Ordinal).ToList();

    public async Task<RetrievalResult> Retrieve(string query, Guid brandId, DocType? docType, int k)
    {
        var result = await _inner.Retrieve(query, brandId, docType, k).ConfigureAwait(false);
        // AgentGrounding maps RetrievedChunk.ChunkId.ToString() into the prompt's grounding block.
        _provenanceIds.AddRange(result.Chunks.Select(chunk => chunk.ChunkId.ToString()));
        return result;
    }
}
