using System.Globalization;
using System.Text.Json;
using Backend.Core.Common;
using Backend.Core.Domain;
using Backend.Core.Knowledge;
using Backend.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace Backend.Infrastructure.Knowledge;

/// <summary>
/// load → chunk → embed(search_document:) → idempotent upsert keyed by deterministic chunk
/// id (DL-026). Runs under the caller's already-bound brand scope; RLS covers every read/write.
/// </summary>
public sealed class KnowledgeIngestService : IKnowledgeIngestService
{
    private readonly AppDbContext _db;
    private readonly IKnowledgeChunker _chunker;
    private readonly IEmbeddingProvider _embeddings;

    public KnowledgeIngestService(AppDbContext db, IKnowledgeChunker chunker, IEmbeddingProvider embeddings)
    {
        _db = db;
        _chunker = chunker;
        _embeddings = embeddings;
    }

    public async Task IngestAsync(Guid docId, CancellationToken cancellationToken = default)
    {
        // RLS scopes this read to the bound brand.
        var doc = await _db.KnowledgeDocs.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == docId, cancellationToken).ConfigureAwait(false);
        if (doc is null)
        {
            return; // not visible to this brand → no-op (degrade, don't crash)
        }

        // True upsert keyed by deterministic chunk id. We do NOT RemoveRange(existing) then
        // Add(same id): EF Core identity resolution throws when a Deleted and an Added entity
        // share a PK in one SaveChanges. Instead: load existing TRACKED, mutate-or-add per
        // draft, then remove any chunk no longer produced (shrink → no orphans). One
        // SaveChanges inside the BrandScope transaction = atomic.
        var existing = await _db.KnowledgeChunks
            .Where(c => c.KnowledgeDocId == docId)
            .ToDictionaryAsync(c => c.Id, cancellationToken).ConfigureAwait(false);

        var metadata = doc.Metadata is null
            ? null
            : JsonSerializer.Deserialize<KnowledgeChunkMetadata>(doc.Metadata);
        var drafts = _chunker.Chunk(doc.DocType, doc.Content, isCompetitor: metadata?.IsCompetitor == true);

        var keptIds = new HashSet<Guid>();
        foreach (var draft in drafts)
        {
            var id = DeterministicGuid.From(docId, draft.Index.ToString(CultureInfo.InvariantCulture));
            keptIds.Add(id);
            var embedding = new Vector(
                await _embeddings.EmbedDocumentAsync(draft.Content, cancellationToken).ConfigureAwait(false));

            if (existing.TryGetValue(id, out var chunk))
            {
                chunk.Content = draft.Content; // mutate the tracked row (no delete+add conflict)
                chunk.Embedding = embedding;
                chunk.DocType = doc.DocType;
                chunk.Facet = doc.Facet;
                chunk.Metadata = doc.Metadata;
            }
            else
            {
                _db.KnowledgeChunks.Add(new KnowledgeChunk
                {
                    Id = id,
                    BrandId = doc.BrandId,
                    KnowledgeDocId = docId,
                    ChunkIndex = draft.Index,
                    DocType = doc.DocType,
                    Facet = doc.Facet,
                    Content = draft.Content,
                    Embedding = embedding,
                    Metadata = doc.Metadata,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            }
        }

        // Shrink: drop chunks whose ids are no longer produced (e.g. a doc edited shorter).
        foreach (var (id, chunk) in existing)
        {
            if (!keptIds.Contains(id))
            {
                _db.KnowledgeChunks.Remove(chunk);
            }
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PurgeAsync(Guid docId, CancellationToken cancellationToken = default)
    {
        var chunks = await _db.KnowledgeChunks
            .Where(c => c.KnowledgeDocId == docId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        _db.KnowledgeChunks.RemoveRange(chunks);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
