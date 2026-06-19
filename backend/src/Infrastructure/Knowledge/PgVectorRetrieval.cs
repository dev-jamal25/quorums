using System.Text.Json;
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
/// <see cref="RetrievalOptions"/>: S0 query transform, S1 dense ∪ sparse recall fused by RRF,
/// S2 cross-encoder rerank + metadata blend. <c>Retrieve</c> is the only public surface. Brand
/// isolation is the RLS policy via the bound <c>BrandScope</c> — never a manual WHERE brand_id
/// (the sparse arm's raw SQL runs on that same scoped connection); <c>docType</c> is an explicit
/// content filter. All-toggles-off reproduces slice-2 dense-only behaviour.
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

    public async Task<RetrievalResult> Retrieve(string query, Guid brandId, DocType? docType, int k)
    {
        var topK = k > 0 ? k : _options.FinalK;
        try
        {
            // S0 — query transform (off → the single original query); a failure degrades to the
            // single query + querytransform.failed, never an exception (DL-022).
            var (variants, s0Degrade) = await VariantsAsync(query).ConfigureAwait(false);

            // S1 — hybrid recall: dense ∪ sparse, deduped, fused by RRF (rank-based, rerank-off order).
            var candidates = await RecallAsync(variants, docType, _options.RecallDepth).ConfigureAwait(false);

            // S2 — rerank + metadata blend; a rerank failure degrades to RRF order + rerank.failed.
            var (ranked, s2Degrade) = await RankAsync(query, candidates, topK).ConfigureAwait(false);

            return new RetrievalResult(ranked, Grounded: ranked.Count > 0, s0Degrade ?? s2Degrade);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Provider/transport failure → structured ToolError, never an exception into the graph (DL-022).
            return new RetrievalResult([], Grounded: false, new ToolError("retrieval.failed", ex.Message, true));
        }
    }

    // S0 — multi-query expansion. The original query is always pooled so a bad paraphrase set never
    // loses it; the reranker still scores the pool against the original. A transformer failure
    // degrades to the single original query + a structured ToolError (never an exception).
    private async Task<(IReadOnlyList<string> Variants, ToolError? Degrade)> VariantsAsync(string query)
    {
        if (!_options.QueryTransformEnabled)
        {
            return ([query], null);
        }

        try
        {
            var expanded = await _transform.ExpandAsync(query, _options.QueryVariants).ConfigureAwait(false);
            var set = new List<string> { query };
            set.AddRange(expanded.Where(v => !string.Equals(v, query, StringComparison.OrdinalIgnoreCase)));
            return (set, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ([query], new ToolError("querytransform.failed", ex.Message, true));
        }
    }

    // S1 — recall = dense ∪ sparse. Each arm yields a rank-ordered list; the union is deduped by
    // chunk id and the per-arm ranks are fused with RRF (k≈60). The RRF score is the rerank-OFF
    // ordering; when rerank is ON it is ignored (the cross-encoder is the ranking authority).
    private async Task<List<Candidate>> RecallAsync(IReadOnlyList<string> variants, DocType? docType, int n)
    {
        var byId = new Dictionary<Guid, Candidate>();
        var rankedLists = new List<IReadOnlyList<Guid>>();

        foreach (var variant in variants)
        {
            if (_options.DenseEnabled)
            {
                var dense = await DenseArmAsync(variant, docType, n).ConfigureAwait(false);
                foreach (var c in dense)
                {
                    byId.TryAdd(c.Id, c);
                }

                rankedLists.Add(dense.Select(c => c.Id).ToList());
            }

            if (_options.SparseEnabled)
            {
                var sparse = await SparseArmAsync(variant, docType, n).ConfigureAwait(false);
                foreach (var c in sparse)
                {
                    byId.TryAdd(c.Id, c);
                }

                rankedLists.Add(sparse.Select(c => c.Id).ToList());
            }
        }

        var rrf = RrfFusion.Fuse(rankedLists, RrfFusion.DefaultK);
        return byId.Values.Select(c => c with { RrfScore = rrf.GetValueOrDefault(c.Id, 0.0) }).ToList();
    }

    // Dense arm — pgvector cosine, returned in rank order (nearest first); the position is the rank
    // RRF consumes. Brand scope is RLS (the bound BrandScope) — NEVER a hand-written WHERE brand_id.
    private async Task<List<Candidate>> DenseArmAsync(string variant, DocType? docType, int n)
    {
        var queryVector = new Vector(await _embeddings.EmbedQueryAsync(variant).ConfigureAwait(false));

        IQueryable<KnowledgeChunk> q = _db.KnowledgeChunks.AsNoTracking().Where(c => c.Embedding != null);
        if (docType is DocType dt)
        {
            // EF translates the enum compare through the existing HasConversion<string>() converter —
            // no magic string, no PascalCase hand-written into SQL.
            q = q.Where(c => c.DocType == dt);   // explicit content filter, not isolation
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
            h.Id, h.KnowledgeDocId, h.Content, h.DocType, h.Facet, h.Metadata, RrfScore: 0.0)).ToList();
    }

    // Sparse arm — Postgres FTS over the unmapped generated search_vector, returned in ts_rank order
    // (the rank RRF consumes). Read-only raw SQL on the BrandScope-bound connection → RLS scopes it
    // (carve-out in .claude/rules/infrastructure.md; never a manual WHERE brand_id).
    private async Task<List<Candidate>> SparseArmAsync(string query, DocType? docType, int n)
    {
        const string cols =
            "SELECT id AS \"Id\", ts_rank_cd(search_vector, websearch_to_tsquery('english', {0}))::float8 AS \"Rank\" " +
            "FROM knowledge_chunks WHERE search_vector @@ websearch_to_tsquery('english', {0}) ";
        var sql = docType is null
            ? cols + "ORDER BY \"Rank\" DESC LIMIT {1}"
            : cols + "AND doc_type = {2} ORDER BY \"Rank\" DESC LIMIT {1}";

        // The default HasConversion<string>() stores the enum member name, so DocType.ToString() IS the
        // stored value (PascalCase) — bound as a parameter ({2}), never a literal in the SQL. (A snake_case
        // literal here would match nothing — DL-033.)
        var hits = docType is null
            ? await _db.Database.SqlQueryRaw<FtsHit>(sql, query, n).ToListAsync().ConfigureAwait(false)
            : await _db.Database.SqlQueryRaw<FtsHit>(sql, query, n, docType.Value.ToString())
                .ToListAsync().ConfigureAwait(false);
        if (hits.Count == 0)
        {
            return [];
        }

        // Re-read entities scoped (also RLS-bound) for Content/Metadata, preserving the ts_rank order.
        var position = hits.Select((h, i) => (h.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var ids = position.Keys.ToList();
        var chunks = await _db.KnowledgeChunks.AsNoTracking()
            .Where(c => ids.Contains(c.Id)).ToListAsync().ConfigureAwait(false);

        return chunks
            .OrderBy(c => position[c.Id])
            .Select(c => new Candidate(
                c.Id, c.KnowledgeDocId, c.Content, c.DocType, c.Facet, c.Metadata, RrfScore: 0.0))
            .ToList();
    }

    // S2 — the cross-encoder is the ranking authority; the per-docType metadata blend (JC-2) then
    // combines normalized relevance with performance/recency. Rerank-off falls back to RRF order;
    // a rerank failure degrades to RRF order + a ToolError (DL-022) — never an exception.
    private async Task<(List<RetrievedChunk> Ranked, ToolError? Degrade)> RankAsync(
        string originalQuery, List<Candidate> candidates, int topK)
    {
        if (!_options.RerankEnabled || candidates.Count == 0)
        {
            var byRrf = candidates.OrderByDescending(c => c.RrfScore).Take(topK)
                .Select(c => ToChunk(c, c.RrfScore)).ToList();
            return (byRrf, null);
        }

        IReadOnlyList<RerankScore> scores;
        try
        {
            // Reranker scores the pool against the ORIGINAL query (variants only widened recall).
            scores = await _rerank.RerankAsync(originalQuery, candidates.Select(c => c.Content).ToList())
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var fallback = candidates.OrderByDescending(c => c.RrfScore).Take(topK)
                .Select(c => ToChunk(c, c.RrfScore)).ToList();
            return (fallback, new ToolError("rerank.failed", ex.Message, true));
        }

        var rel = scores.ToDictionary(s => s.Index, s => s.Relevance);
        var relMin = rel.Values.Min();

        if (!_options.BlendEnabled)
        {
            // Cross-encoder-only (DL-025 diagnostic): order by the pure bge relevance, skipping the
            // per-docType perf/recency blend — isolates the cross-encoder's contribution from the blend's.
            var byRelevance = candidates
                .Select((c, i) => ToChunk(c, rel.GetValueOrDefault(i, relMin)))
                .OrderByDescending(x => x.Score)
                .Take(topK)
                .ToList();
            return (byRelevance, null);
        }

        var relMax = rel.Values.Max();
        var perfRaw = candidates.Select(Performance).ToList();
        var perfMin = perfRaw.Where(p => p.HasValue).Select(p => p!.Value).DefaultIfEmpty(0).Min();
        var perfMax = perfRaw.Where(p => p.HasValue).Select(p => p!.Value).DefaultIfEmpty(0).Max();
        var now = DateTimeOffset.UtcNow;

        var ranked = candidates.Select((c, i) =>
        {
            var relNorm = Normalize(rel.GetValueOrDefault(i, relMin), relMin, relMax);
            var perfNorm = perfRaw[i] is double p ? Normalize(p, perfMin, perfMax) : 0.0;
            var recency = MetadataBlend.RecencyDecay(MetadataOf(c)?.Date, now, _options.Blend.RecencyHalfLifeDays);
            var score = MetadataBlend.Score(relNorm, perfNorm, segmentMatch: 0.0, recency, c.DocType, _options.Blend);
            return ToChunk(c, score);
        }).OrderByDescending(x => x.Score).Take(topK).ToList();

        return (ranked, null);
    }

    private static double Normalize(double v, double min, double max) => max <= min ? 1.0 : (v - min) / (max - min);

    private static RetrievedChunk ToChunk(Candidate c, double score) =>
        new(c.Id, c.DocId, c.Content, c.DocType, c.Facet, score);

    private static double? Performance(Candidate c)
    {
        var m = MetadataOf(c);
        if (m?.EngagementRate is null && m?.Ctr is null)
        {
            return null;
        }

        return ((m.EngagementRate ?? 0) + (m.Ctr ?? 0)) / 2.0;
    }

    private static KnowledgeChunkMetadata? MetadataOf(Candidate c) =>
        c.Metadata is null ? null : JsonSerializer.Deserialize<KnowledgeChunkMetadata>(c.Metadata);

    // Internal recall candidate — carries Metadata for S2's blend (still clean: Content is the only
    // text). RrfScore is the rank-fused recall score (rerank-off ordering); rerank ON ignores it.
    private sealed record Candidate(
        Guid Id, Guid DocId, string Content, DocType DocType, KnowledgeFacet? Facet, string? Metadata,
        double RrfScore);

    private sealed record FtsHit(Guid Id, double Rank);
}
