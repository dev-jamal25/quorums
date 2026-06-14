using Backend.Core.Common;
using Backend.Core.Domain;
using Backend.Core.Knowledge;
using Backend.Core.Multitenancy;
using Backend.Infrastructure.Configuration.Options;
using Backend.Infrastructure.Knowledge;
using Backend.Infrastructure.Knowledge.Seed;
using Backend.Infrastructure.Multitenancy;
using Backend.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Backend.IntegrationTests.Knowledge;

/// <summary>
/// Disposable pgvector Postgres for the RAG slice. Applies the EF migrations (so the
/// real vector/tsvector schema + the InitialCreate RLS policies exist), creates a
/// <b>least-privilege, non-owner</b> application role (the RLS subject — superusers and
/// owners bypass RLS, so the leakage proof must connect as this role), and seeds two
/// brands plus an empty one.
/// </summary>
/// <remarks>
/// The fixture grows across the slice: ingest/retrieval helpers and corpus seeding are
/// added as <c>KnowledgeIngestService</c>, <c>PgVectorRetrieval</c>, and the seeder land.
/// </remarks>
public sealed class KnowledgeFixture : IAsyncLifetime
{
    private const string AppRole = "app_user";

    // Generated per run — no credential literal in the repo.
    private readonly string _appPassword = Guid.NewGuid().ToString("N");

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg16")
        .Build();

    // One deterministic embedder shared by ingest + retrieval so a seeded doc and a query
    // of the same vocabulary land near each other (the relevance proof relies on it).
    private readonly DeterministicEmbeddingProvider _embeddings = new();

    public string SuperuserConnectionString { get; private set; } = string.Empty;

    public string AppUserConnectionString { get; private set; } = string.Empty;

    public Guid BrandA { get; } = Guid.NewGuid();

    public Guid BrandB { get; } = Guid.NewGuid();

    public Guid BrandWithNoCorpus { get; } = Guid.NewGuid();

    /// <summary>Query built from Brand A's distinctive Yirgacheffe product vocabulary.</summary>
    public string BrandAProductQuery { get; } = CoffeeRoasterCorpus.RelevanceQuery;

    /// <summary>The expected nearest chunk for <see cref="BrandAProductQuery"/> under Brand A —
    /// the whole-unit Yirgacheffe product's chunk 0 (id = DeterministicGuid(docId, "0")).</summary>
    public Guid BrandAProductChunkId =>
        DeterministicGuid.From(DeterministicGuid.From(BrandA, CoffeeRoasterCorpus.RelevanceProductTitle), "0");

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        SuperuserConnectionString = _container.GetConnectionString();

        await ApplyMigrationsAsync();
        await CreateLeastPrivilegeRoleAsync();
        AppUserConnectionString = BuildAppUserConnectionString();
        await SeedBrandsAsync();

        // Both brands get the identical corpus, so the leakage proof is separated by RLS alone.
        await SeedCorpusAsync(BrandA);
        await SeedCorpusAsync(BrandB);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>A context on the RLS-subject role with no brand bound — used for catalog
    /// metadata reads. (With no brand bound it sees zero rows; that is RLS doing its job.)</summary>
    public AppDbContext CreateAppContext() => CreateDbContext(AppUserConnectionString);

    /// <summary>A context on the superuser, which bypasses RLS and sees every brand's rows.
    /// Used only to verify chunk ownership across brands in the leakage proof — never the app path.</summary>
    public AppDbContext CreateSuperuserContext() => CreateDbContext(SuperuserConnectionString);

    /// <summary>An ingest service on the RLS-subject role bound to <paramref name="brandId"/>
    /// (the real BrandScope binding path), using the deterministic offline embedder.</summary>
    public (AppDbContext Db, IBrandScope Scope, IKnowledgeIngestService Ingest) CreateIngest(Guid brandId)
    {
        var db = CreateDbContext(AppUserConnectionString);
        var brandContext = new BrandContext();
        brandContext.Bind(brandId);
        var scope = new BrandScope(db, brandContext);
        var ingest = new KnowledgeIngestService(db, new TypeDispatchedChunker(), _embeddings);
        return (db, scope, ingest);
    }

    /// <summary>A dense retrieval service on the RLS-subject role bound to <paramref name="brandId"/>,
    /// sharing the same deterministic embedder as ingest (so a seeded doc and a query of the
    /// same vocabulary are nearest neighbours). Default RetrievalOptions = dense-only.</summary>
    public (AppDbContext Db, IBrandScope Scope, IRetrievalService Retrieval) CreateRetrieval(Guid brandId)
    {
        var db = CreateDbContext(AppUserConnectionString);
        var brandContext = new BrandContext();
        brandContext.Bind(brandId);
        var scope = new BrandScope(db, brandContext);
        var retrieval = new PgVectorRetrieval(db, _embeddings, Options.Create(new RetrievalOptions()));
        return (db, scope, retrieval);
    }

    internal static AppDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, o => o.UseVector())
            .Options;

        return new AppDbContext(options);
    }

    private async Task ApplyMigrationsAsync()
    {
        await using var context = CreateDbContext(SuperuserConnectionString);
        await context.Database.MigrateAsync();
    }

    private async Task CreateLeastPrivilegeRoleAsync()
    {
        await using var admin = CreateDbContext(SuperuserConnectionString);

        // DDL cannot be parameterized; the only interpolated values are a constant role
        // name and a per-run random password (never external input).
        var roleSetup =
            $"""
             DROP ROLE IF EXISTS {AppRole};
             CREATE ROLE {AppRole} LOGIN PASSWORD '{_appPassword}'
                 NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
             GRANT USAGE ON SCHEMA public TO {AppRole};
             GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {AppRole};
             """;

        await admin.Database.ExecuteSqlRawAsync(roleSetup);
    }

    private string BuildAppUserConnectionString() =>
        new NpgsqlConnectionStringBuilder(SuperuserConnectionString)
        {
            Username = AppRole,
            Password = _appPassword,
        }.ConnectionString;

    private async Task SeedBrandsAsync()
    {
        // Seeded through the superuser (bypasses RLS) so rows for all brands exist
        // regardless of binding; the app role then only ever sees its own.
        await using var seed = CreateDbContext(SuperuserConnectionString);
        var now = DateTimeOffset.UtcNow;

        seed.Brands.AddRange(
            new Brand { Id = BrandA, Name = "Roaster A", CreatedAt = now },
            new Brand { Id = BrandB, Name = "Roaster B", CreatedAt = now },
            new Brand { Id = BrandWithNoCorpus, Name = "Empty Roaster", CreatedAt = now });

        await seed.SaveChangesAsync();
    }

    private async Task SeedCorpusAsync(Guid brandId)
    {
        // Runs the production seeder on the RLS-subject role with the deterministic embedder.
        await using var db = CreateDbContext(AppUserConnectionString);
        var brandContext = new BrandContext();
        var scope = new BrandScope(db, brandContext);
        var ingest = new KnowledgeIngestService(db, new TypeDispatchedChunker(), _embeddings);
        var seeder = new KnowledgeSeeder(db, scope, brandContext, ingest);
        await seeder.SeedAsync(brandId);
    }
}
