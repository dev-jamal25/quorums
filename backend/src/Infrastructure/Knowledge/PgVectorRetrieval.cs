using Backend.Core.Domain;
using Backend.Core.Knowledge;
using Backend.Core.Orchestration;
using Backend.Infrastructure.Configuration.Options;
using Backend.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Backend.Infrastructure.Knowledge;

/// <summary>
/// Dense cosine top-k retrieval (DL-025). The four pipeline stages are internal and
/// config-gated (RetrievalOptions); slice 2 wires only the dense recall arm — sparse,
/// rerank, and query-transform are present-but-off, so slice 3 fills them without
/// restructuring. Brand isolation is the RLS policy via the bound BrandScope, never a
/// manual WHERE brand_id; docType is an explicit content filter.
/// </summary>
public sealed class PgVectorRetrieval : IRetrievalService
{
    private readonly AppDbContext _db;
    private readonly IEmbeddingProvider _embeddings;
    private readonly RetrievalOptions _options;

    public PgVectorRetrieval(AppDbContext db, IEmbeddingProvider embeddings, IOptions<RetrievalOptions> options)
    {
        _db = db;
        _embeddings = embeddings;
        _options = options.Value;
    }

    public async Task<RetrievalResult> Retrieve(string query, Guid brandId, string? docType, int k)
    {
        var topK = k > 0 ? k : _options.FinalK;
        try
        {
            // S0 — query transform (slice 3). Off → the single original query.
            var variants = _options.QueryTransformEnabled
                ? throw new NotSupportedException("S0 query transform is a slice-3 stage.")
                : new[] { query };

            // S1 — recall. Slice 2: dense arm only; the sparse union is slice 3 (toggle off).
            var candidates = await DenseRecallAsync(variants, docType, _options.RecallDepth).ConfigureAwait(false);

            // S2 — rank. Slice 3 reranks + blends; slice 2 keeps cosine order and takes top-k.
            var ranked = candidates.Take(topK).ToList();

            return new RetrievalResult(ranked, Grounded: ranked.Count > 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Provider/transport failure → structured ToolError, never an exception into the graph (DL-022).
            return new RetrievalResult([], Grounded: false, new ToolError("retrieval.failed", ex.Message, true));
        }
    }

    private async Task<List<RetrievedChunk>> DenseRecallAsync(IReadOnlyList<string> variants, string? docType, int n)
    {
        if (!_options.DenseEnabled)
        {
            return [];
        }

        var merged = new Dictionary<Guid, RetrievedChunk>();
        foreach (var variant in variants)
        {
            var queryVector = new Vector(await _embeddings.EmbedQueryAsync(variant).ConfigureAwait(false));

            // Brand scope is RLS (the bound BrandScope) — NEVER a hand-written WHERE brand_id.
            IQueryable<KnowledgeChunk> q = _db.KnowledgeChunks.AsNoTracking().Where(c => c.Embedding != null);
            if (docType is not null)
            {
                var parsed = Enum.Parse<DocType>(docType, ignoreCase: true);
                q = q.Where(c => c.DocType == parsed); // explicit content filter, not isolation
            }

            var hits = await q
                .Select(c => new
                {
                    c.Id,
                    c.KnowledgeDocId,
                    c.Content,
                    c.DocType,
                    c.Facet,
                    Distance = c.Embedding!.CosineDistance(queryVector),
                })
                .OrderBy(x => x.Distance)
                .Take(n)
                .ToListAsync()
                .ConfigureAwait(false);

            foreach (var hit in hits)
            {
                merged.TryAdd(
                    hit.Id,
                    new RetrievedChunk(hit.Id, hit.KnowledgeDocId, hit.Content, hit.DocType, hit.Facet, 1.0 - hit.Distance));
            }
        }

        return merged.Values.OrderByDescending(c => c.Score).ToList();
    }
}
