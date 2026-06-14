using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Backend.IntegrationTests.Knowledge;

/// <summary>
/// Proves the KnowledgeVectorSchema migration applies and that the chunk table has the
/// pgvector column, the self-maintaining generated tsvector column, the HNSW + GIN
/// indexes, and is still RLS-covered (already policied in InitialCreate).
/// </summary>
[Trait("Category", "Isolation")]
public sealed class KnowledgeSchemaTests : IClassFixture<KnowledgeFixture>
{
    private readonly KnowledgeFixture _fixture;

    public KnowledgeSchemaTests(KnowledgeFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Migration_applies_and_chunk_table_has_vector_tsvector_and_indexes()
    {
        await using var db = _fixture.CreateAppContext();

        // Generated column present + self-maintaining (GENERATED ALWAYS … STORED).
        var hasGenerated = await db.Database.SqlQueryRaw<bool>(
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns " +
            "WHERE table_name='knowledge_chunks' AND column_name='search_vector' " +
            "AND is_generated='ALWAYS') AS \"Value\"").FirstAsync();
        Assert.True(hasGenerated);

        // HNSW + GIN indexes present.
        var indexes = await db.Database.SqlQueryRaw<string>(
            "SELECT indexname AS \"Value\" FROM pg_indexes WHERE tablename='knowledge_chunks'")
            .ToListAsync();
        Assert.Contains("ix_knowledge_chunks_embedding", indexes);
        Assert.Contains("ix_knowledge_chunks_search_vector", indexes);

        // pgvector embedding column is vector(768).
        var embeddingType = await db.Database.SqlQueryRaw<string>(
            "SELECT format_type(atttypid, atttypmod) AS \"Value\" FROM pg_attribute " +
            "WHERE attrelid='knowledge_chunks'::regclass AND attname='embedding'").FirstAsync();
        Assert.Equal("vector(768)", embeddingType);

        // RLS still enabled on the chunk table (already policied in InitialCreate).
        var rlsEnabled = await db.Database.SqlQueryRaw<bool>(
            "SELECT relrowsecurity AS \"Value\" FROM pg_class WHERE relname='knowledge_chunks'")
            .FirstAsync();
        Assert.True(rlsEnabled);
    }
}
