using System.Text.Json;
using Backend.Core.Common;
using Backend.Core.Domain;
using Backend.Core.Knowledge;
using Backend.Core.Multitenancy;
using Backend.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Backend.Infrastructure.Knowledge.Seed;

/// <summary>
/// Repeatable, idempotent corpus seeder. Doc ids are deterministic (brandId + title), so a
/// re-run updates rather than duplicates; ingest runs through the production
/// <see cref="IKnowledgeIngestService"/> path, so the seeded chunks are exactly what real
/// ingest would produce.
/// </summary>
public sealed class KnowledgeSeeder
{
    private readonly AppDbContext _db;
    private readonly IBrandScope _scope;
    private readonly IBrandContext _brandContext;
    private readonly IKnowledgeIngestService _ingest;

    public KnowledgeSeeder(
        AppDbContext db,
        IBrandScope scope,
        IBrandContext brandContext,
        IKnowledgeIngestService ingest)
    {
        _db = db;
        _scope = scope;
        _brandContext = brandContext;
        _ingest = ingest;
    }

    public async Task SeedAsync(Guid brandId, CancellationToken cancellationToken = default)
    {
        _brandContext.Bind(brandId);
        await using var handle = await _scope.BeginAsync(cancellationToken).ConfigureAwait(false);

        foreach (var spec in CoffeeRoasterCorpus.Specs)
        {
            var docId = DeterministicGuid.From(brandId, spec.Title);
            var metadata = spec.Metadata is null ? null : JsonSerializer.Serialize(spec.Metadata);
            var now = DateTimeOffset.UtcNow;

            var existing = await _db.KnowledgeDocs
                .FirstOrDefaultAsync(d => d.Id == docId, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                _db.KnowledgeDocs.Add(new KnowledgeDoc
                {
                    Id = docId,
                    BrandId = brandId,
                    DocType = spec.DocType,
                    Facet = spec.Facet,
                    Title = spec.Title,
                    Source = spec.Source,
                    Content = spec.Content,
                    Metadata = metadata,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            else
            {
                existing.DocType = spec.DocType;
                existing.Facet = spec.Facet;
                existing.Title = spec.Title;
                existing.Source = spec.Source;
                existing.Content = spec.Content;
                existing.Metadata = metadata;
                existing.UpdatedAt = now;
            }

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await _ingest.IngestAsync(docId, cancellationToken).ConfigureAwait(false);
        }

        await handle.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
