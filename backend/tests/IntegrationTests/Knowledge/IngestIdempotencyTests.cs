using Backend.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Backend.IntegrationTests.Knowledge;

/// <summary>
/// Proves ingest is idempotent (DL-026): re-ingesting a doc replaces its chunks with no
/// duplicates, a doc edited shorter drops its orphan chunks, and purge removes them all.
/// </summary>
[Trait("Category", "Isolation")]
public sealed class IngestIdempotencyTests : IClassFixture<KnowledgeFixture>
{
    private readonly KnowledgeFixture _fixture;

    public IngestIdempotencyTests(KnowledgeFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Reingest_replaces_chunks_no_duplicates_then_delete_purges()
    {
        var (db, scope, ingest) = _fixture.CreateIngest(_fixture.BrandA);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            var doc = new KnowledgeDoc
            {
                Id = Guid.NewGuid(),
                BrandId = _fixture.BrandA,
                DocType = DocType.Product,
                Title = "Ethiopia Light",
                Content = "Floral, citrus, tea-like.",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.KnowledgeDocs.Add(doc);
            await db.SaveChangesAsync();

            await ingest.IngestAsync(doc.Id);
            var first = await db.KnowledgeChunks.AsNoTracking()
                .Where(c => c.KnowledgeDocId == doc.Id).Select(c => c.Id).OrderBy(x => x).ToListAsync();

            await ingest.IngestAsync(doc.Id); // re-ingest (edit/update path)
            var second = await db.KnowledgeChunks.AsNoTracking()
                .Where(c => c.KnowledgeDocId == doc.Id).Select(c => c.Id).OrderBy(x => x).ToListAsync();

            Assert.Equal(first, second); // same ids upserted, no dupes
            Assert.NotEmpty(second);

            await ingest.PurgeAsync(doc.Id);
            var remaining = await db.KnowledgeChunks.AsNoTracking()
                .CountAsync(c => c.KnowledgeDocId == doc.Id);
            Assert.Equal(0, remaining); // delete purges
        }
    }

    [Fact]
    public async Task Reingest_shorter_doc_drops_orphan_chunks_no_leftovers()
    {
        var (db, scope, ingest) = _fixture.CreateIngest(_fixture.BrandA);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();

            // A multi-section playbook → many windowed chunks.
            var longProse = string.Join("\n\n", Enumerable.Range(0, 6)
                .Select(s => string.Join(" ", Enumerable.Range(0, 500).Select(i => $"s{s}w{i}"))));
            var doc = new KnowledgeDoc
            {
                Id = Guid.NewGuid(),
                BrandId = _fixture.BrandA,
                DocType = DocType.BrandPlaybook,
                Facet = KnowledgeFacet.Voice,
                Title = "Voice",
                Content = longProse,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.KnowledgeDocs.Add(doc);
            await db.SaveChangesAsync();

            await ingest.IngestAsync(doc.Id);
            var many = await db.KnowledgeChunks.AsNoTracking().CountAsync(c => c.KnowledgeDocId == doc.Id);
            Assert.True(many > 1);

            doc.Content = "Now just one short section."; // edit shorter → one chunk
            await db.SaveChangesAsync();
            await ingest.IngestAsync(doc.Id); // re-ingest: higher-index chunks become orphans

            var few = await db.KnowledgeChunks.AsNoTracking().CountAsync(c => c.KnowledgeDocId == doc.Id);
            Assert.Equal(1, few); // orphans removed, no leftovers
        }
    }
}
