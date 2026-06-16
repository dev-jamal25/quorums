using Backend.Core.Domain;
using Backend.Core.Knowledge;

namespace Backend.IntegrationTests.Support;

/// <summary>
/// A deterministic, network-free <see cref="IRetrievalService"/> double for the agent tests: returns
/// a fixed set of chunks (filtered by docType), or none (the empty-RAG path → grounded=false). No DB,
/// no embeddings — the RLS-bound real retrieval is exercised by the Category=Isolation suite.
/// </summary>
internal sealed class FakeRetrievalService : IRetrievalService
{
    private readonly IReadOnlyList<RetrievedChunk> _chunks;

    public FakeRetrievalService(IReadOnlyList<RetrievedChunk>? chunks = null) =>
        _chunks = chunks ?? Default();

    /// <summary>An empty retrieval — every slice returns nothing (the ungrounded degrade path, R8).</summary>
    public static FakeRetrievalService Empty() => new([]);

    public Task<RetrievalResult> Retrieve(string query, Guid brandId, DocType? docType, int k)
    {
        var hits = (docType is null ? _chunks : _chunks.Where(chunk => chunk.DocType == docType))
            .Take(k)
            .ToList();
        return Task.FromResult(new RetrievalResult(hits, Grounded: hits.Count > 0));
    }

    private static IReadOnlyList<RetrievedChunk> Default() =>
    [
        new RetrievedChunk(Guid.NewGuid(), Guid.NewGuid(),
            "Our voice is warm, approachable, and unpretentious.", DocType.BrandPlaybook, KnowledgeFacet.Voice, 0.92),
        new RetrievedChunk(Guid.NewGuid(), Guid.NewGuid(),
            "Ethiopia Yirgacheffe: jasmine and bergamot, bright citrus acidity.", DocType.Product, null, 0.88),
    ];
}
