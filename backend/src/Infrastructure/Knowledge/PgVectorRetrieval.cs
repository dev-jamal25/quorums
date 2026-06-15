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
/// The four-stage hybrid retrieval pipeline (DL-025), internal + config-gated behind
/// <see cref="RetrievalOptions"/>: S0 query transform, S1 dense ∪ sparse recall, S2 cross-encoder
/// rerank + metadata blend. <c>Retrieve</c> is the only public surface. Brand isolation is the RLS
/// policy via the bound <c>BrandScope</c> — never a manual WHERE brand_id (the sparse arm's raw SQL
/// runs on that same scoped connection); <c>docType</c> is an explicit content filter.
/// All-toggles-off reproduces slice-2 dense-only behaviour.
/// </summary>
public sealed class PgVectorRetrieval : IRetrievalService
{
    private readonly AppDbContext _db;
    private readonly IEmbeddingProvider _embeddings;
    private readonly IRerankProvider _rerank;
    private readonly IQueryTransformer _transform;
    private readonly RetrievalOptions _options;

    public PgVectorRetrieval(
        AppDbContext db,
        IEmbeddingProvider embeddings,
        IRerankProvider rerank,
        IQueryTransformer transform,
        IOptions<RetrievalOptions> options)
    {
        _db = db;
        _embeddings = embeddings;
        _rerank = rerank;
        _transform = transform;
        _options = options.Value;
    }

    public async Task<RetrievalResult> Retrieve(string query, Guid brandId, string? docType, int k)
    {
        var topK = k > 0 ? k : _options.FinalK;
        try
        {
            // S0 — query transform (off → the single original query).
            var variants = await VariantsAsync(query).ConfigureAwait(false);

            // S1 — hybrid recall: dense ∪ sparse, deduped, unranked.
            var candidates = await RecallAsync(variants, docType, _options.RecallDepth).ConfigureAwait(false);

            // S2 — rank (rerank when enabled; the metadata blend lands in Task 5).
            var ranked = await RankAsync(query, candidates, topK).ConfigureAwait(false);

            return new RetrievalResult(ranked, Grounded: ranked.Count > 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Provider/transport failure → structured ToolError, never an exception into the graph (DL-022).
            return new RetrievalResult([], Grounded: false, new ToolError("retrieval.failed", ex.Message, true));
        }
    }

    // S0 — multi-query expansion. The original query is always pooled so a bad paraphrase set never
    // loses it; the reranker still scores the pool against the original (granular degrade in Task 6).
    private async Task<IReadOnlyList<string>> VariantsAsync(string query)
    {
        if (!_options.QueryTransformEnabled)
        {
            return [query];
        }

        var expanded = await _transform.ExpandAsync(query, _options.QueryVariants).ConfigureAwait(false);
        var set = new List<string> { query };
        set.AddRange(expanded.Where(v => !string.Equals(v, query, StringComparison.OrdinalIgnoreCase)));
        return set;
    }

    // S1 — recall = dense ∪ sparse, deduped by chunk id (merging each arm's score), unranked.
    private async Task<List<Candidate>> RecallAsync(IReadOnlyList<string> variants, string? docType, int n)
    {
        var merged = new Dictionary<Guid, Candidate>();
        foreach (var variant in variants)
        {
            if (_options.DenseEnabled)
            {
                foreach (var c in await DenseArmAsync(variant, docType, n).ConfigureAwait(false))
                {
                    Merge(merged, c);
                }
            }

            if (_options.SparseEnabled)
            {
                foreach (var c in await SparseArmAsync(variant, docType, n).ConfigureAwait(false))
                {
                    Merge(merged, c);
                }
            }
        }

        return merged.Values.ToList();   // unranked recall set; S2 ranks
    }

    private async Task<List<Candidate>> DenseArmAsync(string variant, string? docType, int n)
    {
        var queryVector = new Vector(await _embeddings.EmbedQueryAsync(variant).ConfigureAwait(false));

        // Brand scope is RLS (the bound BrandScope) — NEVER a hand-written WHERE brand_id.
        IQueryable<KnowledgeChunk> q = _db.KnowledgeChunks.AsNoTracking().Where(c => c.Embedding != null);
        if (docType is not null)
        {
            var parsed = Enum.Parse<DocType>(docType, ignoreCase: true);
            q = q.Where(c => c.DocType == parsed);   // explicit content filter, not isolation
        }

        var hits = await q
            .Select(c => new
            {
                c.Id,
                c.KnowledgeDocId,
                c.Content,
                c.DocType,
                c.Facet,
                c.Metadata,
                Distance = c.Embedding!.CosineDistance(queryVector),
            })
            .OrderBy(x => x.Distance)
            .Take(n)
            .ToListAsync()
            .ConfigureAwait(false);

        return hits.Select(h => new Candidate(
            h.Id, h.KnowledgeDocId, h.Content, h.DocType, h.Facet, h.Metadata,
            DenseScore: 1.0 - h.Distance, SparseScore: 0.0)).ToList();
    }

    private async Task<List<Candidate>> SparseArmAsync(string query, string? docType, int n)
    {
        // Read-only FTS on the BrandScope-bound connection → RLS scopes it (carve-out in
        // .claude/rules/infrastructure.md; never a manual WHERE brand_id). docType is an explicit
        // content filter, parameterized. Project (id, ts_rank_cd) so the FTS rank survives (item 4).
        const string cols =
            "SELECT id AS \"Id\", ts_rank_cd(search_vector, websearch_to_tsquery('english', {0}))::float8 AS \"Rank\" " +
            "FROM knowledge_chunks WHERE search_vector @@ websearch_to_tsquery('english', {0}) ";
        var sql = docType is null
            ? cols + "ORDER BY \"Rank\" DESC LIMIT {1}"
            : cols + "AND doc_type = {2} ORDER BY \"Rank\" DESC LIMIT {1}";

        var hits = docType is null
            ? await _db.Database.SqlQueryRaw<FtsHit>(sql, query, n).ToListAsync().ConfigureAwait(false)
            : await _db.Database.SqlQueryRaw<FtsHit>(sql, query, n,
                  Enum.Parse<DocType>(docType, ignoreCase: true).ToString()).ToListAsync().ConfigureAwait(false);
        if (hits.Count == 0)
        {
            return [];
        }

        var rank = hits.ToDictionary(h => h.Id, h => h.Rank);
        var ids = rank.Keys.ToList();

        // Scoped entity re-read (also RLS-bound) for Content/Metadata the blend needs.
        var chunks = await _db.KnowledgeChunks.AsNoTracking()
            .Where(c => ids.Contains(c.Id)).ToListAsync().ConfigureAwait(false);

        return chunks.Select(c => new Candidate(
            c.Id, c.KnowledgeDocId, c.Content, c.DocType, c.Facet, c.Metadata,
            DenseScore: 0.0, SparseScore: rank[c.Id])).ToList();
    }

    // S2 — the cross-encoder is the ranking authority. The per-docType metadata blend lands in Task 5;
    // for now rerank produces pure relevance order, and rerank-off falls back to recall-score order.
    private async Task<List<RetrievedChunk>> RankAsync(string originalQuery, List<Candidate> candidates, int topK)
    {
        if (!_options.RerankEnabled || candidates.Count == 0)
        {
            // Dense-only → cosine (slice-2 parity); sparse-only → ts_rank; both → the stronger signal.
            return candidates.OrderByDescending(RecallScore).Take(topK)
                .Select(c => ToChunk(c, RecallScore(c))).ToList();
        }

        // Reranker scores the pool against the ORIGINAL query (variants only widened recall).
        var scores = await _rerank.RerankAsync(originalQuery, candidates.Select(c => c.Content).ToList())
            .ConfigureAwait(false);
        var rel = scores.ToDictionary(s => s.Index, s => s.Relevance);

        return candidates.Select((c, i) => ToChunk(c, rel.GetValueOrDefault(i, 0.0)))
            .OrderByDescending(x => x.Score).Take(topK).ToList();
    }

    // Dedup by chunk id, keeping the max of each arm's score so a chunk found by BOTH arms carries
    // its dense cosine AND its FTS rank (item 4 — needed for the rerank-OFF ordering).
    private static void Merge(Dictionary<Guid, Candidate> acc, Candidate c) =>
        acc[c.Id] = acc.TryGetValue(c.Id, out var e)
            ? e with { DenseScore = Math.Max(e.DenseScore, c.DenseScore), SparseScore = Math.Max(e.SparseScore, c.SparseScore) }
            : c;

    private static double RecallScore(Candidate c) => Math.Max(c.DenseScore, c.SparseScore);

    private static RetrievedChunk ToChunk(Candidate c, double score) =>
        new(c.Id, c.DocId, c.Content, c.DocType, c.Facet, score);

    // Internal recall candidate — carries Metadata for S2's blend (still clean: Content is the only text).
    private sealed record Candidate(
        Guid Id, Guid DocId, string Content, DocType DocType, KnowledgeFacet? Facet, string? Metadata,
        double DenseScore, double SparseScore);

    private sealed record FtsHit(Guid Id, double Rank);
}
