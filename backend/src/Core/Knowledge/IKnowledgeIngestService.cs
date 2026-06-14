namespace Backend.Core.Knowledge;

/// <summary>
/// Ingests a knowledge doc into pgvector: chunk → embed(search_document:) → idempotent
/// upsert keyed by chunk id (DL-026). Runs under the caller's already-bound brand scope,
/// so writes are RLS-covered exactly like reads.
/// </summary>
public interface IKnowledgeIngestService
{
    /// <summary>Chunk + embed a doc and upsert its chunks. Idempotent: re-ingest replaces
    /// this doc's chunks (no duplicates; a doc edited shorter drops its orphan chunks).</summary>
    Task IngestAsync(Guid docId, CancellationToken cancellationToken = default);

    /// <summary>Purge all chunks for a doc.</summary>
    Task PurgeAsync(Guid docId, CancellationToken cancellationToken = default);
}
