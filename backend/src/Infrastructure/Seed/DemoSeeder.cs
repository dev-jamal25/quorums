using Backend.Core.Onboarding;
using Backend.Infrastructure.Knowledge.Seed;
using Backend.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Backend.Infrastructure.Seed;

/// <summary>
/// Idempotent demo seed for the coffee-roaster brand. Goes through the PRODUCTION paths — the
/// onboarding handler (RLS-respecting: <c>IBrandContext</c> + the brand scope, never an unscoped
/// insert) for Brand + the rich <c>BrandProfile</c> + the Transit-encrypted <c>BrandMetaConnection</c>,
/// then <see cref="KnowledgeSeeder"/> for the RAG corpus. Idempotent so the Phase-5 cold-start
/// rehearsal repeats: the brand is keyed by <see cref="DemoBrand.Name"/> (skip-if-exists, onboarding
/// mints the id exactly once), and <see cref="KnowledgeSeeder"/> upserts by deterministic doc id — a
/// re-run after a fresh migrate never duplicates.
/// </summary>
public sealed class DemoSeeder
{
    private readonly IServiceScopeFactory _scopeFactory;

    public DemoSeeder(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    /// <summary>
    /// Seeds (or confirms) the demo brand. <paramref name="includeKnowledge"/> controls the RAG corpus
    /// step, which needs TEI up for embeddings — pass <c>false</c> to land brand + profile + Meta
    /// connection alone (that already unblocks the gate/publish flow).
    /// </summary>
    public async Task<DemoSeedResult> RunAsync(bool includeKnowledge, CancellationToken cancellationToken = default)
    {
        Guid brandId;
        bool created;

        // Brand identity, idempotent by name. `brands` is the scope root (not RLS-scoped), so the
        // existence check needs no bound brand; the onboarding handler self-scopes when it creates.
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var existing = await db.Brands.AsNoTracking()
                .FirstOrDefaultAsync(b => b.Name == DemoBrand.Name, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                brandId = existing.Id;
                created = false;
            }
            else
            {
                var onboarding = scope.ServiceProvider.GetRequiredService<IBrandOnboardingService>();
                brandId = await onboarding.OnboardAsync(DemoBrand.Command, cancellationToken).ConfigureAwait(false);
                created = true;
            }
        }

        // Knowledge corpus, idempotent upsert through the production ingest path. A fresh scope gives a
        // clean IBrandContext bound to the brand we just resolved.
        var knowledgeSeeded = false;
        if (includeKnowledge)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var seeder = scope.ServiceProvider.GetRequiredService<KnowledgeSeeder>();
            await seeder.SeedAsync(brandId, cancellationToken).ConfigureAwait(false);
            knowledgeSeeded = true;
        }

        return new DemoSeedResult(brandId, created, knowledgeSeeded);
    }
}

/// <summary>The outcome of <see cref="DemoSeeder.RunAsync"/>: the demo brand id, whether it was newly
/// created (vs already present), and whether the knowledge corpus was seeded this run.</summary>
public sealed record DemoSeedResult(Guid BrandId, bool BrandCreated, bool KnowledgeSeeded);
