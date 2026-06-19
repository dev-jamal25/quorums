using Backend.Core.Domain;
using Backend.Core.Knowledge;
using Backend.Core.Multitenancy;
using Backend.Infrastructure.Configuration.Options;
using Backend.Infrastructure.Evaluation;
using Backend.Infrastructure.Knowledge;
using Backend.Infrastructure.Knowledge.Seed;
using Backend.Infrastructure.Multitenancy;
using Backend.Infrastructure.Persistence;
using Backend.IntegrationTests.Knowledge;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Backend.IntegrationTests.Eval;

/// <summary>
/// The reference-based retrieval-eval fixture (DL-048/025). Unlike <see cref="KnowledgeFixture"/> (which
/// seeds with the deterministic offline embedder), this seeds the fixed demo brand's 13-chunk corpus with
/// the <b>real self-hosted nomic-embed</b> (tei-embed) and retrieves through the <b>real bge cross-encoder</b>
/// (tei-rerank) — so the dense + rerank stages carry genuine signal for the paired ablation. S0 stays the
/// deterministic multi-query mock (no LLM). The self-hosted services need NO API keys.
/// </summary>
/// <remarks>
/// If tei-embed / tei-rerank are not reachable (as in CI, which never runs TEI), <see cref="ServicesAvailable"/>
/// is false and the corpus is left unseeded; the ablation test then skips EXPLICITLY rather than passing on
/// an empty result — mirroring the opt-in live-TEI tests.
/// </remarks>
public sealed class RetrievalAblationFixture : IAsyncLifetime, IDisposable
{
    private const string AppRole = "app_user";
    private readonly string _appPassword = Guid.NewGuid().ToString("N");

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg16")
        .Build();

    private HttpClient _embedHttp = default!;
    private HttpClient _rerankHttp = default!;
    private NomicEmbeddingProvider _embeddings = default!;
    private CrossEncoderRerankProvider _rerank = default!;

    /// <summary>The fixed demo brand the committed golden set is authored against (eval/datasets/552732e7-…).</summary>
    public static Guid DemoBrand => KnowledgeFixture.DemoBrand;

    public string SuperuserConnectionString { get; private set; } = string.Empty;

    public string AppUserConnectionString { get; private set; } = string.Empty;

    /// <summary>True once the self-hosted tei-embed + tei-rerank are reachable AND the corpus is seeded.</summary>
    public bool ServicesAvailable { get; private set; }

    /// <summary>Why the services were judged unavailable (for an explicit skip message).</summary>
    public string? UnavailableReason { get; private set; }

    public string StorageRoot { get; } =
        Environment.GetEnvironmentVariable(EvalReportingFactory.StorageRootEnvVar) is { Length: > 0 } configured
            ? configured
            : Path.Combine(Path.GetTempPath(), "quorums-eval-ablation-" + Guid.NewGuid().ToString("N"));

    private static string EmbedBaseUrl =>
        "http://" + (Environment.GetEnvironmentVariable("Embeddings__Endpoint") ?? "localhost:8090");

    private static string RerankBaseUrl =>
        "http://" + (Environment.GetEnvironmentVariable("Reranker__Endpoint") ?? "localhost:8091");

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        SuperuserConnectionString = _container.GetConnectionString();

        await ApplyMigrationsAsync();
        await CreateLeastPrivilegeRoleAsync();
        AppUserConnectionString = BuildAppUserConnectionString();
        await SeedBrandAsync();

        _embedHttp = new HttpClient { BaseAddress = new Uri(EmbedBaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        _rerankHttp = new HttpClient { BaseAddress = new Uri(RerankBaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        _embeddings = new NomicEmbeddingProvider(
            _embedHttp,
            Options.Create(new EmbeddingsOptions { BaseUrl = EmbedBaseUrl, Model = "nomic-embed-text-v1.5", Dimension = 768 }));
        _rerank = new CrossEncoderRerankProvider(_rerankHttp);

        var (reachable, reason) = await ProbeServicesAsync();
        if (!reachable)
        {
            UnavailableReason = reason;
            ServicesAvailable = false;
            return;
        }

        // Seed the 13-chunk demo corpus with REAL embeddings (the dense arm's signal depends on it).
        await SeedDemoCorpusAsync();
        ServicesAvailable = true;
    }

    public void Dispose()
    {
        _embedHttp?.Dispose();
        _rerankHttp?.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();

        var isTempStore = StorageRoot.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase);
        if (!isTempStore)
        {
            return;
        }

        try
        {
            if (Directory.Exists(StorageRoot))
            {
                Directory.Delete(StorageRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp report store.
        }
    }

    /// <summary>The configured real bge cross-encoder reranker (so a test can wrap it in a counting spy).</summary>
    public IRerankProvider Reranker => _rerank;

    /// <summary>
    /// A demo-brand-scoped retrieval over the real nomic-embed + bge-rerank, config-gated by
    /// <paramref name="options"/>. <paramref name="rerankOverride"/> (e.g. a counting spy) replaces the real
    /// reranker when supplied — for proving the S2 stage is skipped under default-off.
    /// </summary>
    public (AppDbContext Db, IBrandScope Scope, IRetrievalService Retrieval) CreateRetrieval(
        RetrievalOptions options, IRerankProvider? rerankOverride = null)
    {
        var db = CreateDbContext(AppUserConnectionString);
        var brandContext = new BrandContext();
        brandContext.Bind(DemoBrand);
        var scope = new BrandScope(db, brandContext);
        var retrieval = new PgVectorRetrieval(
            db, _embeddings, rerankOverride ?? _rerank, new DeterministicQueryTransformer(), Options.Create(options));
        return (db, scope, retrieval);
    }

    /// <summary>A demo-brand-scoped <see cref="EvalRunPersistence"/> (the production dual-write path).</summary>
    public (EvalRunPersistence Persistence, AppDbContext Db) CreatePersistence()
    {
        var db = CreateDbContext(AppUserConnectionString);
        var brandContext = new BrandContext();
        brandContext.Bind(DemoBrand);
        return (new EvalRunPersistence(db, new BrandScope(db, brandContext)), db);
    }

    /// <summary>A demo-brand-scoped read context for verifying the persisted rows.</summary>
    public (AppDbContext Db, IBrandScope Scope) CreateBrandScopedContext()
    {
        var db = CreateDbContext(AppUserConnectionString);
        var brandContext = new BrandContext();
        brandContext.Bind(DemoBrand);
        return (db, new BrandScope(db, brandContext));
    }

    private async Task<(bool Reachable, string? Reason)> ProbeServicesAsync()
    {
        var embed = await ProbeAsync(_embedHttp);
        if (!embed)
        {
            return (false, $"tei-embed not reachable at {EmbedBaseUrl}");
        }

        var rerank = await ProbeAsync(_rerankHttp);
        return rerank ? (true, null) : (false, $"tei-rerank not reachable at {RerankBaseUrl}");
    }

    private static async Task<bool> ProbeAsync(HttpClient http)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var resp = await http.GetAsync("/health", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    private static AppDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, o => o.UseVector())
            .Options;
        return new AppDbContext(options);
    }

    private async Task SeedDemoCorpusAsync()
    {
        await using var db = CreateDbContext(AppUserConnectionString);
        var brandContext = new BrandContext();
        var scope = new BrandScope(db, brandContext);
        var ingest = new KnowledgeIngestService(db, new TypeDispatchedChunker(), _embeddings);
        var seeder = new KnowledgeSeeder(db, scope, brandContext, ingest);
        await seeder.SeedAsync(DemoBrand);
    }

    private async Task SeedBrandAsync()
    {
        await using var seed = CreateDbContext(SuperuserConnectionString);
        seed.Brands.Add(new Brand { Id = DemoBrand, Name = "Demo Roaster (ablation)", CreatedAt = DateTimeOffset.UtcNow });
        await seed.SaveChangesAsync();
    }

    private async Task ApplyMigrationsAsync()
    {
        await using var context = CreateDbContext(SuperuserConnectionString);
        await context.Database.MigrateAsync();
    }

    private async Task CreateLeastPrivilegeRoleAsync()
    {
        await using var admin = CreateDbContext(SuperuserConnectionString);
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
}
