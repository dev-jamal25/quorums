# RAG Ingest + Dense Retrieval (Slice 2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Honour the `brand-knowledge-rag`, `dotnet-engineering-standards`, and `marketing-agency-architecture` skills throughout. The `brand-knowledge-rag` skill is the **frozen design contract — re-decide nothing in it**. Apply TDD (superpowers:test-driven-development) on every unit.

**Goal:** A provable, brand-isolated RAG thin loop — a doc ingested through `chunk → embed(search_document:) → pgvector`, retrievable by a dense `IRetrievalService.Retrieve(query, brandId, docType?, k)` that returns only the calling brand's chunks. The full `KnowledgeChunk` schema (vector **+** generated tsvector) ships now so slice 3 layers sparse/rerank/query-transform behind config toggles with **no new migration**.

**Architecture:** Extend the already-present, already-RLS-policied skeletal `KnowledgeDoc`/`KnowledgeChunk` entities with **one** EF migration that adds the `vector(768)` column, a `GENERATED ALWAYS AS (to_tsvector('english', content)) STORED` column + GIN index, an HNSW (`vector_cosine_ops`) index, the `DocType`/`Facet` enums, and `jsonb` metadata. `IEmbeddingProvider` (`NomicEmbeddingProvider` HTTP→`tei-embed` + deterministic CI mock, gated by `Embeddings:Mode`) applies the `search_document:`/`search_query:` prefix split. A type-dispatched chunker (DL-026) and a `KnowledgeIngestService` (shared by a Hangfire ingest job and the seeder) do `load → chunk → embed → idempotent upsert keyed by chunk id`. `PgVectorRetrieval : IRetrievalService` does dense cosine top-k behind a **config-gated stage seam** (only the dense stage is wired; sparse/rerank/query-transform are present-but-off). **Brand isolation is inherited from Postgres RLS via the `BrandScope` interceptor — never a manual `WHERE brand_id`.**

**Tech Stack:** .NET 10, EF Core 9.0.2 / Npgsql 9.0.2, `Pgvector` + `Pgvector.EntityFrameworkCore`, Postgres + pgvector (`pgvector/pgvector:pg16`), Hangfire on Postgres, HF TEI (`nomic-embed-text-v1.5`), xUnit + Testcontainers.

---

## Context (The Why)

The capstone grounds its generation agents in a per-brand knowledge base. This slice (slice 2, branch `feat/rag-ingest-retrieval`) builds the **thin loop**: ingest a brand's doc into pgvector and retrieve it back, **dense-only**, with the leakage proof that brand A never sees brand B's chunks. The four-stage hybrid pipeline (sparse FTS, cross-encoder rerank, query-transform, metadata blend) is **slice 3** — but the **schema** for it ships now (the `tsvector` column self-populates and the stage seam exists) so slice 3 adds no migration and no ingest change.

The entities `KnowledgeDoc` and `KnowledgeChunk` already exist as skeletons ([backend/src/Core/Domain/KnowledgeDoc.cs](backend/src/Core/Domain/KnowledgeDoc.cs), [backend/src/Core/Domain/KnowledgeChunk.cs](backend/src/Core/Domain/KnowledgeChunk.cs)), are already `DbSet`s on [AppDbContext](backend/src/Infrastructure/Persistence/AppDbContext.cs), and already have RLS policies in [the InitialCreate migration](backend/src/Infrastructure/Persistence/Migrations/20260612010630_InitialCreate.cs) (`knowledge_docs` / `knowledge_chunks` are in its `BrandScopedTables()` loop). So this slice **extends** them; it does not create them.

**The uncertain part is the schema/migration, not the C#.** Per the brief, Task 1 is a **throwaway spike** that proves the EF↔pgvector mechanics (vector round-trip, HNSW via raw SQL, generated tsvector, clean apply from an empty volume, RLS already attached) before any production code is built on them. Everything else is mechanical against patterns this repo already uses.

**Intended outcome:** `POST/PUT/DELETE /brands/{…}/knowledge` ingest docs into pgvector for a brand; `IRetrievalService.Retrieve` returns that brand's nearest chunks and nothing from another brand; a repeatable two-brand coffee-roaster seed corpus exists; and the five adversarial proofs (isolation, prefix correctness, ingest idempotency, empty-degrade, dense relevance) are green. All CLAUDE.md gates stay green.

## Frozen givens (from the `brand-knowledge-rag` skill — do NOT re-decide)

- **Isolation is inherited, never manual.** Brand scope = the RLS policy via the `BrandScope` interceptor's `set_config('app.current_brand', …, true)`. **Never** `.Where(c => c.BrandId == brandId)` as the isolation mechanism. `docType` **is** a legitimate `.Where` (content filter); `brandId` is **not**.
- **Prefixes are mandatory and matched.** `search_document:` at ingest, `search_query:` at retrieval. Two methods, not one with a flag — a mismatch must be a test failure, not luck.
- **pgvector column dim == model output dim == 768.** Enforced in the migration; the embedding dim is config-bound (`EmbeddingsOptions.Dimension`).
- **Metadata is structured, never embedded into chunk text.** Structured fields ride in typed columns / `jsonb`; the chunk `content` stays clean.
- **Atomic content is never split.** `historical_post`, `product`/FAQ, competitor copy, `platform_guidance` heuristics are whole-unit. `brand_playbook` + `market_intel` article are section-aware windows (~400–600 tok / ~60 overlap).
- **Degrade, don't crash.** Empty recall → `Grounded == false`, no exception. Provider failure → structured `ToolError`, never an exception into the graph.
- **Ingest is idempotent.** Re-ingest replaces a doc's chunks (no dupes); DELETE purges.
- **Every stage is config-gated and independently toggleable** (the slice-3 / Phase-9 ablation precondition). Only the dense stage is implemented now; the rest are off-by-default no-ops.

## Out of scope (slice 3+ — do NOT build here)

Sparse FTS **query** (the `tsvector` column exists + self-populates, but no `@@`/`websearch_to_tsquery` yet), cross-encoder rerank (`tei-rerank` / `IRerankProvider`), query-transform (`IQueryTransformer`), the metadata blend, generation agents consuming RAG. Leave the stage seam and the populated `tsvector` ready, **not wired**.

---

## Confirmed EF / pgvector facts (FILL IN from the Task 1 spike, then delete the spike)

> Task 1 writes the throwaway spike, confirms each item below against the **real** packages, records the confirmed shape here, then deletes the spike. Tasks 2+ are written against the **assumed** shapes; reconcile each against this cheat-sheet before reusing it. If a name/signature moved, **update this plan's samples in-place and continue.**

> **✅ CONFIRMED by the Task 1 spike (2026-06-14, against `pgvector/pgvector:pg16`):** `Pgvector` **0.3.2** + `Pgvector.EntityFrameworkCore` **0.3.0** build clean on EF Core **9.0.2** / Npgsql **9.0.2** (0 warnings). `UseNpgsql(cs, o => o.UseVector())` on the **EF options with a bare connection string is sufficient** — no separate `NpgsqlDataSource.UseVector()` needed. `modelBuilder.HasPostgresExtension("vector")` + `EnsureCreatedAsync` creates the extension and the `vector(n)` column. `Vector` round-trips; `.OrderBy(r => r.Embedding!.CosineDistance(query))` (extension on `Pgvector.Vector`, `using Pgvector;`) translates to `<=>` and orders nearest-first. The generated `tsvector` column, the GIN index, and the HNSW (`vector_cosine_ops`) index all apply via raw SQL and the generated column self-populates. Reading the unmapped column works via `SqlQueryRaw<string>("SELECT search_vector::text AS \"Value\" …")`. **Caveats for Task 2+:** (1) the generated-column SQL references the **snake_case** column `content` — fine in production because `AppDbContext`'s loop snake_cases every column; (2) pass `float[]` vectors as **locals**, not inline constant-array args (repo treats CA1861 as an error).

- **Packages (confirm with `dotnet package search`, do NOT assume):** `Pgvector.EntityFrameworkCore` is currently pinned at **0.2.0** in `Directory.Packages.props` (unreferenced). Latest is **0.3.0**; base `Pgvector` latest is **0.3.2**. EF Core here is **9.0.2** — **default action: bump the pin to `Pgvector.EntityFrameworkCore` 0.3.0 and add `Pgvector` 0.3.2** (0.3.x is the EF Core 9 line). Confirm 0.3.0 binds on EF 9.0.2 / Npgsql 9.0.2 in the spike; if not, record the version that does.
- **Where each package is referenced:** `Pgvector` (base, defines the `Vector` type) → **`Core.csproj`** (the `KnowledgeChunk.Embedding` property lives in Core). `Pgvector.EntityFrameworkCore` (the EF mapping + `UseVector()`) → **`Infrastructure.csproj`**. This is Core's first NuGet dependency — deliberate, minimal (base `Pgvector` only, no EF/Npgsql leakage into Core).
- **Enabling the type:** `options.UseNpgsql(cs, o => o.UseVector())` in [DataAccessServiceCollectionExtensions](backend/src/Infrastructure/Persistence/DataAccessServiceCollectionExtensions.cs). Confirm whether 0.3.0 also needs an `NpgsqlDataSource` `UseVector()` registration or whether the EF options call suffices with a bare connection string. **Record the confirmed minimal wiring.**
- **Extension creation:** `modelBuilder.HasPostgresExtension("vector")` in `OnModelCreating` → EF emits `CREATE EXTENSION IF NOT EXISTS vector` ordered first in the migration. Confirm this is what the differ generates (vs needing a raw `migrationBuilder.Sql`).
- **Vector column:** `entity.Property(c => c.Embedding).HasColumnType("vector(768)")` → EF `AddColumn` emits `vector(768)`. Confirm round-trip: write a `Vector`, read it back equal.
- **Cosine query operator:** confirm the LINQ-translatable form. Expected `.OrderBy(c => c.Embedding!.CosineDistance(queryVector))` (translates to `<=>`). Record the exact method/namespace (`using Pgvector;`).
- **HNSW index:** EF cannot express `USING hnsw`. Confirmed approach = `migrationBuilder.Sql("CREATE INDEX … USING hnsw (embedding vector_cosine_ops);")`. Confirm it builds on an empty table.
- **Generated tsvector:** confirmed approach = **raw SQL generated column, NOT mapped on the entity in slice 2** — `ALTER TABLE knowledge_chunks ADD COLUMN search_vector tsvector GENERATED ALWAYS AS (to_tsvector('english', content)) STORED;` + a GIN index, both via `migrationBuilder.Sql`. EF never tracks it (so the model differ won't drop it), it self-maintains, and slice 3 maps it when it wires FTS. Confirm a later `migrations add` produces **no** spurious operation against the unmapped column. **Reading an unmapped column uses raw SQL** (`db.Database.SqlQueryRaw<string>("SELECT search_vector::text …")`) — `EF.Property` throws on a column absent from the model.
- **RLS:** `knowledge_docs` / `knowledge_chunks` are **already** in `BrandScopedTables()` in InitialCreate, so the policy already exists — this migration adds **no** RLS SQL. Confirm the spike migration applies clean and the chunk table is still policy-covered after the `ALTER`s.
- **Empty-volume apply:** confirm `docker compose down -v` then migrate-up applies the new migration with no error.

---

## File structure

**Core** (`backend/src/Core/`) — entities, enums, boundary interfaces, contracts:
- Create `Domain/DocType.cs` — enum: `BrandPlaybook | HistoricalPost | Product | MarketIntel | PlatformGuidance`.
- Create `Domain/KnowledgeFacet.cs` — enum: `Voice | Persona | Mission | VisualStyle`.
- Modify `Domain/KnowledgeDoc.cs` — add `DocType DocType`, `KnowledgeFacet? Facet`, `string? Source`, `string? Metadata` (jsonb-backed).
- Modify `Domain/KnowledgeChunk.cs` — add `DocType DocType`, `KnowledgeFacet? Facet`, `Vector? Embedding`, `string? Metadata`; add `public const int EmbeddingDimension = 768;`.
- Create `Knowledge/IEmbeddingProvider.cs` — `EmbedDocumentAsync` / `EmbedQueryAsync`.
- Create `Knowledge/IRetrievalService.cs` — the interface + `RetrievalResult` + `RetrievedChunk` records.
- Create `Knowledge/IKnowledgeChunker.cs` — `IKnowledgeChunker` + `ChunkDraft` record.
- Create `Knowledge/IKnowledgeIngestService.cs` — `IngestAsync(Guid docId, CancellationToken)` / `PurgeAsync(Guid docId, CancellationToken)`.
- Create `Knowledge/KnowledgeChunkMetadata.cs` — typed metadata record (nullable performance/recency/segment/product/platform fields) serialized to the `jsonb` column.
- Reuse existing `Backend.Core.Orchestration.Contracts.ToolError` for failure results.

**Infrastructure** (`backend/src/Infrastructure/`):
- Create `Knowledge/NomicEmbeddingProvider.cs` — typed `HttpClient` → `tei-embed`, applies prefixes, validates returned dim == `EmbeddingsOptions.Dimension`, Polly timeout/retry, returns `ToolError` on failure.
- Create `Knowledge/DeterministicEmbeddingProvider.cs` — CI mock: hashed token-frequency vector (shared vocabulary → cosine proximity), L2-normalized, applies + records prefixes. Lives in Infrastructure (like `MockMetaIntegration`) so app + tests share it.
- Create `Knowledge/TypeDispatchedChunker.cs` — dispatch on `DocType` to two primitives (whole-unit / section-aware window).
- Create `Knowledge/KnowledgeIngestService.cs` — `load → chunk → embed(search_document:) → upsert keyed by chunk id`; `Purge`. Shared by the job and the seeder.
- Create `Knowledge/PgVectorRetrieval.cs` — `IRetrievalService`; dense cosine top-k behind the config-gated stage seam.
- Create `Knowledge/Seed/CoffeeRoasterCorpus.cs` — static two-brand corpus data.
- Create `Knowledge/Seed/KnowledgeSeeder.cs` — repeatable idempotent seeder (calls `KnowledgeIngestService`).
- Create `Knowledge/KnowledgeServiceCollectionExtensions.cs` — `AddKnowledge(config)`: embeddings `Mode` switch, chunker, ingest service, retrieval, seeder.
- Create `Configuration/Options/RetrievalOptions.cs` — stage toggles + `N` + `K` + blend-weight placeholders (all config-bound).
- Modify `Configuration/Options/EmbeddingsOptions.cs` — add `Mode` (`nomic|mock`) and the two prefix constants.
- Modify `Persistence/AppDbContext.cs` — `HasPostgresExtension("vector")`; configure the new columns (enum→text, `vector(768)`, `jsonb` metadata converter).
- Modify `Persistence/DataAccessServiceCollectionExtensions.cs` — `o => o.UseVector()`.
- Create `Jobs/IngestKnowledgeDocJob.cs` — Hangfire entrypoint (binds brand scope, calls `KnowledgeIngestService`).
- Modify `Jobs/HangfireServiceCollectionExtensions.cs` — `services.AddScoped<IngestKnowledgeDocJob>()`.
- Create `Persistence/Migrations/<timestamp>_KnowledgeVectorSchema.cs` — the one migration (generated, then hand-append the raw SQL blocks).

**Api** (`backend/src/Api/`):
- Create `Controllers/KnowledgeController.cs` — `POST`/`PUT`/`DELETE` under `[Route("brands/{brandId:guid}/knowledge")]` (or read brand from `X-Brand-Id` via the existing middleware — see Task 7).
- Create `Dtos/CreateKnowledgeDocRequest.cs` + `CreateKnowledgeDocRequestValidator.cs`.
- Create `Dtos/UpdateKnowledgeDocRequest.cs` + `UpdateKnowledgeDocRequestValidator.cs`.
- Create `Controllers/KnowledgeSeedController.cs` (dev-only, env-gated) — `POST` triggers `KnowledgeSeeder`.
- Modify `Program.cs` (Api **and** Worker) — call `builder.Services.AddKnowledge(builder.Configuration)`.
- Modify `HealthChecks/HealthCheckRegistration.cs` — gate the embeddings URL check on `Embeddings:Mode != "mock"` (mirrors the Vault feature-gate).
- Modify `appsettings.json` — add `Embeddings:Mode: "nomic"` + a `Retrieval` section.

**Tests** (`backend/tests/`):
- Create `IntegrationTests/Support/RecordingHttpMessageHandler.cs` — records outbound TEI request bodies (for the prefix test, no network).
- Create `IntegrationTests/Support/RecordingEmbeddingProvider.cs` — wraps `IEmbeddingProvider`, records which prefix path was called.
- Create `IntegrationTests/Knowledge/KnowledgeFixture.cs` — `pgvector/pgvector:pg16` Testcontainer, applies migrations, two-brand brand-scoped context helpers, seeds corpus.
- Create `IntegrationTests/Knowledge/RagIsolationTests.cs` — `[Trait("Category","Isolation")]`.
- Create `IntegrationTests/Knowledge/PrefixCorrectnessTests.cs`.
- Create `IntegrationTests/Knowledge/IngestIdempotencyTests.cs`.
- Create `IntegrationTests/Knowledge/EmptyRetrievalTests.cs`.
- Create `IntegrationTests/Knowledge/DenseRelevanceTests.cs` — mock-seeded; plus an opt-in `[Trait("Category","LiveEmbeddings")]` real-`tei-embed` case.
- Create `UnitTests/Knowledge/TypeDispatchedChunkerTests.cs`.

---

## Task 0: Land this plan in the repo

Keep the plan version-controlled alongside the decision logs and the maf plan. (The auto-generated `~/.claude/plans/…-lantern.md` slug is a throwaway name.)

- [ ] **Step 1:** Write this plan to `docs/superpowers/plans/rag-ingest-retrieval.md` in the repo (matches the existing `maf-orchestrator-seam.md` naming convention — no date prefix).
- [ ] **Step 2:** Commit it.

```bash
git add docs/superpowers/plans/rag-ingest-retrieval.md
git commit -m "docs(rag): slice-2 ingest + dense retrieval implementation plan"
```

- [ ] **Step 3:** STOP. Do not begin Task 1 until the user says "start".

---

## Task 1: pgvector / EF / HNSW / generated-tsvector spike (validate the one uncertain thing)

**Files:**
- Modify: `backend/Directory.Packages.props`, `backend/src/Core/Core.csproj`, `backend/src/Infrastructure/Infrastructure.csproj`, `backend/src/Infrastructure/Persistence/DataAccessServiceCollectionExtensions.cs`
- Test (throwaway, deleted at end of task): `backend/tests/IntegrationTests/Knowledge/PgVectorSpikeTests.cs`

- [ ] **Step 1: Confirm package ids + versions (do NOT assume).** Run:

```bash
dotnet package search Pgvector.EntityFrameworkCore --format json
dotnet package search Pgvector --format json
```

Record the latest stable versions in the cheat-sheet above. Default: `Pgvector.EntityFrameworkCore` **0.3.0**, `Pgvector` **0.3.2**.

- [ ] **Step 2: Pin + reference.** In `backend/Directory.Packages.props`, bump the existing pin and add the base package:

```xml
<PackageVersion Include="Pgvector.EntityFrameworkCore" Version="0.3.0" />
<PackageVersion Include="Pgvector" Version="0.3.2" />
```

Add `<PackageReference Include="Pgvector" />` to `backend/src/Core/Core.csproj`, and `<PackageReference Include="Pgvector.EntityFrameworkCore" />` to `backend/src/Infrastructure/Infrastructure.csproj`.

- [ ] **Step 3: Enable the type mapping.** In `DataAccessServiceCollectionExtensions.cs`, change `options.UseNpgsql(connectionString);` to:

```csharp
options.UseNpgsql(connectionString, o => o.UseVector());
```

- [ ] **Step 4: Write a throwaway spike test** that proves the full mechanic end-to-end against a real `pgvector/pgvector:pg16` Testcontainer. It must: create the extension, create a tiny table with a `vector(3)` column **and** a generated `tsvector` column **and** an HNSW index **and** a GIN index via raw SQL, write a row, and run a cosine-ordered query. Use this to confirm the exact `UseVector` wiring, the `Vector` round-trip, and the `CosineDistance` translation.

```csharp
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Backend.IntegrationTests.Knowledge;

[Trait("Category", "Spike")]
public sealed class PgVectorSpikeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg16").Build();

    public Task InitializeAsync() => _pg.StartAsync();
    public Task DisposeAsync() => _pg.DisposeAsync().AsTask();

    private sealed class SpikeContext(DbContextOptions<SpikeContext> o) : DbContext(o)
    {
        public DbSet<SpikeRow> Rows => Set<SpikeRow>();
        protected override void OnModelCreating(ModelBuilder b)
        {
            b.HasPostgresExtension("vector");
            b.Entity<SpikeRow>(e =>
            {
                e.ToTable("spike");
                e.HasKey(x => x.Id);
                e.Property(x => x.Embedding).HasColumnType("vector(3)");
            });
        }
    }
    private sealed class SpikeRow { public int Id { get; set; } public string Content { get; set; } = ""; public Vector? Embedding { get; set; } }

    [Fact]
    public async Task Vector_roundtrips_and_cosine_orders_nearest_first()
    {
        var opts = new DbContextOptionsBuilder<SpikeContext>()
            .UseNpgsql(_pg.GetConnectionString(), o => o.UseVector()).Options;

        await using var db = new SpikeContext(opts);
        await db.Database.EnsureCreatedAsync();

        // Raw SQL the production migration will mirror: generated tsvector + GIN + HNSW.
        await db.Database.ExecuteSqlRawAsync(
            "ALTER TABLE spike ADD COLUMN search_vector tsvector " +
            "GENERATED ALWAYS AS (to_tsvector('english', content)) STORED;");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX ix_spike_sv ON spike USING gin (search_vector);");
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX ix_spike_emb ON spike USING hnsw (embedding vector_cosine_ops);");

        db.Rows.AddRange(
            new SpikeRow { Id = 1, Content = "espresso roast", Embedding = new Vector(new float[] { 1f, 0f, 0f }) },
            new SpikeRow { Id = 2, Content = "green tea",       Embedding = new Vector(new float[] { 0f, 1f, 0f }) });
        await db.SaveChangesAsync();

        var query = new Vector(new float[] { 0.9f, 0.1f, 0f });
        var nearest = await db.Rows.AsNoTracking()
            .OrderBy(r => r.Embedding!.CosineDistance(query)).First().Id;
        Assert.Equal(1, nearest);  // proves cosine translation + HNSW-compatible ordering

        // Generated column self-populated. Read via RAW SQL — search_vector is deliberately
        // unmapped (not even a shadow property), so EF.Property would throw "property could
        // not be found". This is exactly how slice 3 will read it until it maps it.
        var sv = await db.Database
            .SqlQueryRaw<string>("SELECT search_vector::text AS \"Value\" FROM spike WHERE id = 1")
            .FirstAsync();
        Assert.False(string.IsNullOrEmpty(sv));
    }
}
```

Run: `dotnet test backend/Backend.sln --filter "FullyQualifiedName~PgVectorSpikeTests" -v minimal`. Expected: PASS.

- [ ] **Step 5: Reconcile + record.** Update the **Confirmed EF / pgvector facts** cheat-sheet with the exact: `UseVector` wiring, `CosineDistance` namespace/signature, whether `HasPostgresExtension` alone emits the extension in a migration, and any deviation. If the `EF.Property<string?>("search_vector")` read shape differs, note the confirmed read form (slice 3 needs it).
- [ ] **Step 6: Delete the spike.** `git rm backend/tests/IntegrationTests/Knowledge/PgVectorSpikeTests.cs`.
- [ ] **Step 7: Build gate.** Run: `dotnet build backend/Backend.sln -warnaserror`. Expected: PASS (the new package refs bind clean on EF 9.0.2).
- [ ] **Step 8: Commit.**

```bash
git add backend/Directory.Packages.props backend/src/Core/Core.csproj backend/src/Infrastructure/Infrastructure.csproj backend/src/Infrastructure/Persistence/DataAccessServiceCollectionExtensions.cs
git commit -m "build(rag): pin pgvector EF packages + UseVector; validated EF/pgvector/HNSW/tsvector seam"
```

---

## Task 2: Enums + entity extensions + EF config + the one migration

**Files:**
- Create: `backend/src/Core/Domain/DocType.cs`, `backend/src/Core/Domain/KnowledgeFacet.cs`, `backend/src/Core/Knowledge/KnowledgeChunkMetadata.cs`
- Modify: `backend/src/Core/Domain/KnowledgeDoc.cs`, `backend/src/Core/Domain/KnowledgeChunk.cs`, `backend/src/Infrastructure/Persistence/AppDbContext.cs`
- Create (generated, then hand-edited): `backend/src/Infrastructure/Persistence/Migrations/<ts>_KnowledgeVectorSchema.cs`
- Test: `backend/tests/IntegrationTests/Knowledge/KnowledgeSchemaTests.cs`

- [ ] **Step 1: Add the enums.**

```csharp
// backend/src/Core/Domain/DocType.cs
namespace Backend.Core.Domain;

/// <summary>The five corpus document types (DL-026). Drives the chunker dispatch,
/// the chunk metadata shape, and the retrieval pre-filter — kept in lock-step.</summary>
public enum DocType
{
    BrandPlaybook,
    HistoricalPost,
    Product,
    MarketIntel,
    PlatformGuidance,
}
```

```csharp
// backend/src/Core/Domain/KnowledgeFacet.cs
namespace Backend.Core.Domain;

/// <summary>brand_playbook facet — lets each agent pull only its slice (DL-026).</summary>
public enum KnowledgeFacet
{
    Voice,
    Persona,
    Mission,
    VisualStyle,
}
```

- [ ] **Step 2: Add the typed metadata record.** Structured fields ride here, **never** in chunk text.

```csharp
// backend/src/Core/Knowledge/KnowledgeChunkMetadata.cs
namespace Backend.Core.Knowledge;

/// <summary>Structured fields promoted from a KnowledgeDoc onto its chunks at ingest.
/// Serialized to the chunk's jsonb column; read structurally by slice 3's filter/blend.
/// NEVER concatenated into chunk text before embedding (DL-026).</summary>
public sealed record KnowledgeChunkMetadata
{
    public double? EngagementRate { get; init; }
    public double? Ctr { get; init; }
    public string? AudienceSegment { get; init; }
    public string? Objective { get; init; }
    public DateTimeOffset? Date { get; init; }
    public string? ProductId { get; init; }
    public decimal? Price { get; init; }
    public string? Category { get; init; }
    public string? Source { get; init; }
    public bool? IsCompetitor { get; init; }
    public string? Platform { get; init; }
    public string? Surface { get; init; }
}
```

- [ ] **Step 3: Extend the entities.**

```csharp
// backend/src/Core/Domain/KnowledgeDoc.cs — add to the existing class:
public DocType DocType { get; set; }
public KnowledgeFacet? Facet { get; set; }
public string? Source { get; set; }
public string? Metadata { get; set; }   // jsonb (serialized KnowledgeChunkMetadata)
```

```csharp
// backend/src/Core/Domain/KnowledgeChunk.cs — add `using Pgvector;` and to the existing class:
public const int EmbeddingDimension = 768;   // MUST equal EmbeddingsOptions.Dimension and the column dim

public DocType DocType { get; set; }
public KnowledgeFacet? Facet { get; set; }
public Vector? Embedding { get; set; }       // vector(768); null until embedded
public string? Metadata { get; set; }        // jsonb (serialized KnowledgeChunkMetadata)
```

- [ ] **Step 4: Configure EF.** In `AppDbContext.OnModelCreating`, before the global snake_case loop, add:

```csharp
modelBuilder.HasPostgresExtension("vector");

modelBuilder.Entity<KnowledgeDoc>(e =>
{
    e.Property(d => d.DocType).HasConversion<string>().HasMaxLength(32);
    e.Property(d => d.Facet).HasConversion<string>().HasMaxLength(16);
    e.Property(d => d.Metadata).HasColumnType("jsonb");
});

modelBuilder.Entity<KnowledgeChunk>(e =>
{
    e.Property(c => c.DocType).HasConversion<string>().HasMaxLength(32);
    e.Property(c => c.Facet).HasConversion<string>().HasMaxLength(16);
    e.Property(c => c.Embedding).HasColumnType($"vector({KnowledgeChunk.EmbeddingDimension})");
    e.Property(c => c.Metadata).HasColumnType("jsonb");
    // search_vector is added by raw SQL in the migration and intentionally NOT mapped here (slice 2).
});
```

> The existing `OnModelCreating` loops auto-add the `ix_*_brand_id` index and snake_case every column, so `DocType` → `doc_type`, `KnowledgeDocId` → `knowledge_doc_id`, etc. Do not duplicate those.

- [ ] **Step 5: Generate the migration.** Run:

```bash
dotnet ef migrations add KnowledgeVectorSchema -p backend/src/Infrastructure -s backend/src/Api
```

Verify the generated `Up()` contains: an `AlterDatabase` annotation creating the `vector` extension, `AddColumn` for `embedding vector(768)`, `doc_type`, `facet`, `metadata` (both tables), `source` (docs). It must **not** contain any `CreateTable` (the tables already exist) and **not** touch RLS.

- [ ] **Step 6: Hand-append the raw SQL** EF cannot express, at the **end** of `Up()` (after the `AddColumn` ops so `embedding` and `content` exist):

```csharp
// Generated, self-maintaining sparse-search column — slice 3 FTS queries it; slice 2 only populates it.
migrationBuilder.Sql(
    "ALTER TABLE knowledge_chunks ADD COLUMN search_vector tsvector " +
    "GENERATED ALWAYS AS (to_tsvector('english', content)) STORED;");

// Sparse arm index (slice 3).
migrationBuilder.Sql(
    "CREATE INDEX ix_knowledge_chunks_search_vector ON knowledge_chunks USING gin (search_vector);");

// Dense arm index — cosine HNSW (EF cannot express USING hnsw).
migrationBuilder.Sql(
    "CREATE INDEX ix_knowledge_chunks_embedding ON knowledge_chunks USING hnsw (embedding vector_cosine_ops);");
```

And at the **start** of `Down()`:

```csharp
migrationBuilder.Sql("DROP INDEX IF EXISTS ix_knowledge_chunks_embedding;");
migrationBuilder.Sql("DROP INDEX IF EXISTS ix_knowledge_chunks_search_vector;");
migrationBuilder.Sql("ALTER TABLE knowledge_chunks DROP COLUMN IF EXISTS search_vector;");
```

- [ ] **Step 7: Write the schema test** (proves the migration applies from empty + the columns/indexes/RLS exist).

```csharp
[Trait("Category", "Isolation")]
public sealed class KnowledgeSchemaTests : IClassFixture<KnowledgeFixture>   // fixture lands in Task 7; stub minimal here if running early
{
    // ... fixture in ctor ...

    [Fact]
    public async Task Migration_applies_and_chunk_table_has_vector_tsvector_and_indexes()
    {
        await using var db = _fixture.CreateAppContext();   // unscoped/superuser
        // generated column present + self-maintaining:
        var hasGenerated = await db.Database.SqlQueryRaw<bool>(
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns " +
            "WHERE table_name='knowledge_chunks' AND column_name='search_vector' " +
            "AND is_generated='ALWAYS') AS \"Value\"").FirstAsync();
        Assert.True(hasGenerated);

        // HNSW + GIN indexes present:
        var idx = await db.Database.SqlQueryRaw<string>(
            "SELECT indexname AS \"Value\" FROM pg_indexes WHERE tablename='knowledge_chunks'").ToListAsync();
        Assert.Contains("ix_knowledge_chunks_embedding", idx);
        Assert.Contains("ix_knowledge_chunks_search_vector", idx);

        // RLS still enabled on the chunk table (already policied in InitialCreate):
        var rls = await db.Database.SqlQueryRaw<bool>(
            "SELECT relrowsecurity AS \"Value\" FROM pg_class WHERE relname='knowledge_chunks'").FirstAsync();
        Assert.True(rls);
    }
}
```

- [ ] **Step 8: Run it, expect PASS.** Run: `dotnet test backend/Backend.sln --filter "FullyQualifiedName~KnowledgeSchemaTests"`.
- [ ] **Step 9: Build + format gates.** `dotnet build backend/Backend.sln -warnaserror` then `dotnet format backend/Backend.sln --verify-no-changes`. Expected PASS.
- [ ] **Step 10: Commit.** `git commit -m "feat(rag): KnowledgeDoc/Chunk vector+tsvector schema + RLS-covered migration"`

---

## Task 3: IEmbeddingProvider — deterministic mock, Nomic HTTP provider, Mode switch, dim guard

**Files:**
- Create: `backend/src/Core/Knowledge/IEmbeddingProvider.cs`, `backend/src/Infrastructure/Knowledge/DeterministicEmbeddingProvider.cs`, `backend/src/Infrastructure/Knowledge/NomicEmbeddingProvider.cs`
- Modify: `backend/src/Infrastructure/Configuration/Options/EmbeddingsOptions.cs`
- Test: `backend/tests/UnitTests/Knowledge/DeterministicEmbeddingProviderTests.cs`, `backend/tests/IntegrationTests/Knowledge/PrefixCorrectnessTests.cs`, `backend/tests/IntegrationTests/Support/RecordingHttpMessageHandler.cs`

- [ ] **Step 1: Define the interface** (two methods, not one with a flag — DL-016).

```csharp
// backend/src/Core/Knowledge/IEmbeddingProvider.cs
namespace Backend.Core.Knowledge;

public interface IEmbeddingProvider
{
    /// <summary>Embeds a corpus chunk. Applies the "search_document:" prefix.</summary>
    Task<float[]> EmbedDocumentAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Embeds a query. Applies the "search_query:" prefix.</summary>
    Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Add prefixes + Mode to EmbeddingsOptions.**

```csharp
// add to backend/src/Infrastructure/Configuration/Options/EmbeddingsOptions.cs
public const string DocumentPrefix = "search_document:";
public const string QueryPrefix = "search_query:";

/// <summary>"nomic" (real tei-embed) or "mock" (deterministic, offline). CI uses mock.</summary>
public string Mode { get; init; } = "nomic";
```

- [ ] **Step 3: Failing test for the deterministic mock** — shared vocabulary ⇒ cosine proximity; correct dim; prefixes recorded.

```csharp
public sealed class DeterministicEmbeddingProviderTests
{
    [Fact]
    public async Task Shared_vocabulary_is_nearer_than_disjoint_and_dim_is_768()
    {
        var p = new DeterministicEmbeddingProvider();
        var doc = await p.EmbedDocumentAsync("single origin espresso roast notes");
        var near = await p.EmbedQueryAsync("espresso roast");
        var far  = await p.EmbedQueryAsync("matcha green tea ceremony");

        Assert.Equal(KnowledgeChunk.EmbeddingDimension, doc.Length);
        Assert.True(Cosine(doc, near) > Cosine(doc, far));   // semantic-ish ordering for the relevance test
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0; for (var i = 0; i < a.Length; i++) dot += a[i] * b[i]; return dot; // both L2-normalized
    }
}
```

- [ ] **Step 4: Run it, expect FAIL** (`DeterministicEmbeddingProvider` not defined).

- [ ] **Step 5: Implement the deterministic mock.**

```csharp
// backend/src/Infrastructure/Knowledge/DeterministicEmbeddingProvider.cs
using Backend.Core.Domain;
using Backend.Core.Knowledge;
using Backend.Infrastructure.Configuration.Options;

namespace Backend.Infrastructure.Knowledge;

/// <summary>Offline, deterministic embedding for CI (DL-016). Hashed token-frequency
/// vector: shared vocabulary ⇒ cosine proximity, so the dense-relevance test is meaningful
/// without a model server. Applies + records the prefix exactly like the real provider.</summary>
public sealed class DeterministicEmbeddingProvider : IEmbeddingProvider
{
    private const int Dim = KnowledgeChunk.EmbeddingDimension;
    public string? LastDocumentPrefix { get; private set; }
    public string? LastQueryPrefix { get; private set; }

    public Task<float[]> EmbedDocumentAsync(string text, CancellationToken ct = default)
    {
        LastDocumentPrefix = EmbeddingsOptions.DocumentPrefix;
        return Task.FromResult(Embed(text));
    }

    public Task<float[]> EmbedQueryAsync(string text, CancellationToken ct = default)
    {
        LastQueryPrefix = EmbeddingsOptions.QueryPrefix;
        return Task.FromResult(Embed(text));
    }

    private static float[] Embed(string text)
    {
        var v = new float[Dim];
        foreach (var token in text.ToLowerInvariant().Split(
                     (char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = new string(token.Where(char.IsLetterOrDigit).ToArray());
            if (clean.Length == 0) continue;
            var slot = (int)(unchecked((uint)StableHash(clean)) % Dim);
            v[slot] += 1f;
        }
        var norm = (float)Math.Sqrt(v.Sum(x => (double)x * x));
        if (norm > 0) for (var i = 0; i < Dim; i++) v[i] /= norm;
        return v;
    }

    private static int StableHash(string s)
    {
        unchecked { var h = 17; foreach (var c in s) h = h * 31 + c; return h; }
    }
}
```

- [ ] **Step 6: Run it, expect PASS.**

- [ ] **Step 7: Implement the Nomic HTTP provider** (typed `HttpClient`, prefix applied, dim guard, `ToolError` on failure). Follow `dotnet-engineering-standards` for Polly + structured errors.

```csharp
// backend/src/Infrastructure/Knowledge/NomicEmbeddingProvider.cs
using System.Net.Http.Json;
using Backend.Core.Domain;
using Backend.Core.Knowledge;
using Backend.Infrastructure.Configuration.Options;
using Microsoft.Extensions.Options;

namespace Backend.Infrastructure.Knowledge;

/// <summary>nomic-embed-text-v1.5 via HF TEI (tei-embed). Applies the search_document:/
/// search_query: prefix split (DL-016) and asserts the returned dim == configured dim.</summary>
public sealed class NomicEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _http;
    private readonly int _dim;

    public NomicEmbeddingProvider(HttpClient http, IOptions<EmbeddingsOptions> options)
    {
        _http = http;
        _dim = options.Value.Dimension;
    }

    public Task<float[]> EmbedDocumentAsync(string text, CancellationToken ct = default) =>
        EmbedAsync(EmbeddingsOptions.DocumentPrefix + text, ct);

    public Task<float[]> EmbedQueryAsync(string text, CancellationToken ct = default) =>
        EmbedAsync(EmbeddingsOptions.QueryPrefix + text, ct);

    private async Task<float[]> EmbedAsync(string prefixed, CancellationToken ct)
    {
        // TEI /embed contract: { "inputs": "<prefixed text>" } -> [[...768 floats...]]
        using var resp = await _http.PostAsJsonAsync("/embed", new { inputs = prefixed }, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var vectors = await resp.Content.ReadFromJsonAsync<float[][]>(ct).ConfigureAwait(false);
        var vec = vectors is { Length: > 0 } ? vectors[0] : [];
        if (vec.Length != _dim)
            throw new InvalidOperationException(
                $"Embedding dim {vec.Length} != configured {_dim}; pgvector column is vector({KnowledgeChunk.EmbeddingDimension}).");
        return vec;
    }
}
```

> The `BaseAddress` (scheme prepended to the `host:port` `Embeddings:Endpoint`), Polly timeout/retry policy, and the failure→`ToolError` mapping are wired in `AddKnowledge` (Task 6 / DI). The HTTP-layer exception is caught at the call sites (ingest, retrieval) and turned into a `ToolError` — never thrown into the graph (DL-022).

- [ ] **Step 8: Write the prefix-correctness test** against the **real** `NomicEmbeddingProvider` over an in-memory recording handler (no network, no TEI — satisfies "CI never calls TEI").

```csharp
// backend/tests/IntegrationTests/Support/RecordingHttpMessageHandler.cs
public sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    public List<string> RequestBodies { get; } = [];
    private readonly string _responseJson;
    public RecordingHttpMessageHandler(string responseJson) => _responseJson = responseJson;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        RequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = new StringContent(_responseJson, System.Text.Encoding.UTF8, "application/json") };
    }
}
```

```csharp
// backend/tests/IntegrationTests/Knowledge/PrefixCorrectnessTests.cs
public sealed class PrefixCorrectnessTests
{
    private static NomicEmbeddingProvider Provider(RecordingHttpMessageHandler h) =>
        new(new HttpClient(h) { BaseAddress = new Uri("http://tei-embed") },
            Options.Create(new EmbeddingsOptions { BaseUrl = "tei-embed:80", Model = "nomic", Dimension = 768 }));

    private static string Ok768() => "[[" + string.Join(",", Enumerable.Repeat("0.1", 768)) + "]]";

    [Fact]
    public async Task Document_embed_uses_search_document_prefix()
    {
        var h = new RecordingHttpMessageHandler(Ok768());
        await Provider(h).EmbedDocumentAsync("roasted in small batches");
        Assert.Contains("search_document:roasted in small batches", h.RequestBodies[0]);
        Assert.DoesNotContain("search_query:", h.RequestBodies[0]);   // mismatch would be caught here
    }

    [Fact]
    public async Task Query_embed_uses_search_query_prefix()
    {
        var h = new RecordingHttpMessageHandler(Ok768());
        await Provider(h).EmbedQueryAsync("what is your roast style");
        Assert.Contains("search_query:what is your roast style", h.RequestBodies[0]);
        Assert.DoesNotContain("search_document:", h.RequestBodies[0]);
    }
}
```

- [ ] **Step 9: Run them, expect PASS.** Run: `dotnet test backend/Backend.sln --filter "FullyQualifiedName~PrefixCorrectnessTests|FullyQualifiedName~DeterministicEmbeddingProviderTests"`.
- [ ] **Step 10: Build + format gates.** Expected PASS.
- [ ] **Step 11: Commit.** `git commit -m "feat(rag): IEmbeddingProvider — Nomic HTTP provider + deterministic CI mock + prefix split + dim guard"`

---

## Task 4: Type-dispatched chunker (DL-026)

One pipeline, dispatch on `DocType` to two primitives: whole-unit (atomic) and section-aware window. Structured fields → metadata, never embedded.

**Files:**
- Create: `backend/src/Core/Knowledge/IKnowledgeChunker.cs`, `backend/src/Infrastructure/Knowledge/TypeDispatchedChunker.cs`
- Test: `backend/tests/UnitTests/Knowledge/TypeDispatchedChunkerTests.cs`

- [ ] **Step 1: Define the interface + result.**

```csharp
// backend/src/Core/Knowledge/IKnowledgeChunker.cs
using Backend.Core.Domain;

namespace Backend.Core.Knowledge;

public sealed record ChunkDraft(int Index, string Content);

public interface IKnowledgeChunker
{
    /// <summary>Splits a doc's raw content per its DocType. Whole-unit types yield exactly
    /// one chunk; section-aware types yield N windowed chunks. Structured metadata stays
    /// out of the chunk text (DL-026). <paramref name="isCompetitor"/> drives the
    /// market_intel sub-dispatch (DL-026): competitor copy is atomic → whole-unit, an
    /// article is prose → section-aware window. Ignored for every other DocType.</summary>
    IReadOnlyList<ChunkDraft> Chunk(DocType docType, string rawContent, bool isCompetitor = false);
}
```

- [ ] **Step 2: Failing tests** — whole-unit never splits; section-aware windows split long prose; overlap preserved; clean text (no metadata).

```csharp
public sealed class TypeDispatchedChunkerTests
{
    private readonly IKnowledgeChunker _chunker = new TypeDispatchedChunker();

    [Theory]
    [InlineData(DocType.HistoricalPost)]
    [InlineData(DocType.Product)]
    [InlineData(DocType.PlatformGuidance)]
    public void Whole_unit_types_are_never_split(DocType type)
    {
        var longText = string.Join(" ", Enumerable.Repeat("word", 2000));
        var chunks = _chunker.Chunk(type, longText);
        Assert.Single(chunks);
        Assert.Equal(longText, chunks[0].Content);
    }

    [Fact]
    public void Brand_playbook_prose_is_windowed_with_overlap()
    {
        var prose = string.Join(" ", Enumerable.Range(0, 1500).Select(i => $"w{i}"));
        var chunks = _chunker.Chunk(DocType.BrandPlaybook, prose);
        Assert.True(chunks.Count > 1);                                   // windowed
        Assert.True(chunks.Zip(chunks.Skip(1)).Any(p =>                  // overlap exists
            Overlaps(p.First.Content, p.Second.Content)));
        Assert.All(chunks, c => Assert.DoesNotContain("engagement_rate", c.Content)); // text stays clean
    }

    [Fact]
    public void Market_intel_competitor_copy_is_whole_unit_but_article_is_windowed()
    {
        var prose = string.Join(" ", Enumerable.Range(0, 1500).Select(i => $"w{i}"));
        // DL-026 sub-dispatch: competitor caption is atomic → whole-unit regardless of length.
        Assert.Single(_chunker.Chunk(DocType.MarketIntel, prose, isCompetitor: true));
        // An article is prose → section-aware window.
        Assert.True(_chunker.Chunk(DocType.MarketIntel, prose, isCompetitor: false).Count > 1);
    }

    private static bool Overlaps(string a, string b)
    {
        var tail = a.Split(' ').TakeLast(10);
        var head = b.Split(' ').Take(20).ToHashSet();
        return tail.Any(head.Contains);
    }
}
```

- [ ] **Step 3: Run, expect FAIL.**

- [ ] **Step 4: Implement the chunker.** Token estimate is whitespace-word count (no tokenizer dependency); window ≈ 450 words (~600 tok) with ~45-word (~60 tok) overlap, split on blank-line sections first.

```csharp
// backend/src/Infrastructure/Knowledge/TypeDispatchedChunker.cs
using Backend.Core.Domain;
using Backend.Core.Knowledge;

namespace Backend.Infrastructure.Knowledge;

public sealed class TypeDispatchedChunker : IKnowledgeChunker
{
    private const int WindowWords = 450;   // ~600 tokens
    private const int OverlapWords = 45;    // ~60 tokens

    public IReadOnlyList<ChunkDraft> Chunk(DocType docType, string rawContent, bool isCompetitor = false)
    {
        var text = rawContent?.Trim() ?? string.Empty;
        if (text.Length == 0) return [];

        return docType switch
        {
            // brand_playbook prose → section-aware window.
            DocType.BrandPlaybook => Windowed(text),
            // market_intel sub-dispatch (DL-026): competitor copy is atomic → whole-unit;
            // an article is prose → section-aware window.
            DocType.MarketIntel => isCompetitor ? [new ChunkDraft(0, text)] : Windowed(text),
            // Whole-unit for atomic content (historical_post, product/FAQ, platform_guidance).
            _ => [new ChunkDraft(0, text)],
        };
    }

    private static IReadOnlyList<ChunkDraft> Windowed(string text)
    {
        // Split on blank-line "sections" so a window never straddles unrelated headings.
        var sections = text.Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries);
        var chunks = new List<ChunkDraft>();
        var index = 0;
        foreach (var section in sections)
        {
            var words = section.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length <= WindowWords)
            {
                chunks.Add(new ChunkDraft(index++, section.Trim()));
                continue;
            }
            for (var start = 0; start < words.Length; start += WindowWords - OverlapWords)
            {
                var slice = words.Skip(start).Take(WindowWords);
                chunks.Add(new ChunkDraft(index++, string.Join(' ', slice)));
                if (start + WindowWords >= words.Length) break;
            }
        }
        return chunks;
    }
}
```

- [ ] **Step 5: Run, expect PASS.**
- [ ] **Step 6: Build + format gates.** Expected PASS.
- [ ] **Step 7: Commit.** `git commit -m "feat(rag): type-dispatched chunker — whole-unit + section-aware window (DL-026)"`

---

## Task 5: Ingest service + Hangfire job + KnowledgeController CRUD (idempotent)

**Files:**
- Create: `backend/src/Core/Knowledge/IKnowledgeIngestService.cs`, `backend/src/Infrastructure/Knowledge/KnowledgeIngestService.cs`, `backend/src/Infrastructure/Jobs/IngestKnowledgeDocJob.cs`
- Create: `backend/src/Api/Controllers/KnowledgeController.cs`, `backend/src/Api/Dtos/CreateKnowledgeDocRequest.cs` (+validator), `UpdateKnowledgeDocRequest.cs` (+validator)
- Modify: `backend/src/Infrastructure/Jobs/HangfireServiceCollectionExtensions.cs`
- Test: `backend/tests/IntegrationTests/Knowledge/IngestIdempotencyTests.cs`

- [ ] **Step 1: Define the ingest service contract.**

```csharp
// backend/src/Core/Knowledge/IKnowledgeIngestService.cs
namespace Backend.Core.Knowledge;

public interface IKnowledgeIngestService
{
    /// <summary>Chunk + embed(search_document:) a doc and UPSERT its chunks keyed by chunk id.
    /// Idempotent: re-ingest replaces this doc's chunks, no duplicates. Runs under the
    /// caller's already-bound brand scope.</summary>
    Task IngestAsync(Guid docId, CancellationToken cancellationToken = default);

    /// <summary>Purge all chunks for a doc.</summary>
    Task PurgeAsync(Guid docId, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Failing idempotency test** (re-ingest replaces, delete purges, deterministic chunk ids).

```csharp
[Trait("Category", "Isolation")]
public sealed class IngestIdempotencyTests : IClassFixture<KnowledgeFixture>
{
    private readonly KnowledgeFixture _fixture;
    public IngestIdempotencyTests(KnowledgeFixture f) => _fixture = f;

    [Fact]
    public async Task Reingest_replaces_chunks_no_duplicates_then_delete_purges()
    {
        var (db, scope, ingest) = _fixture.CreateIngest(_fixture.BrandA);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();
            var doc = new KnowledgeDoc { Id = Guid.NewGuid(), BrandId = _fixture.BrandA,
                DocType = DocType.Product, Title = "Ethiopia Light", Content = "Floral, citrus, tea-like.",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            db.KnowledgeDocs.Add(doc);
            await db.SaveChangesAsync();

            await ingest.IngestAsync(doc.Id);
            var first = await db.KnowledgeChunks.AsNoTracking().Where(c => c.KnowledgeDocId == doc.Id)
                .Select(c => c.Id).OrderBy(x => x).ToListAsync();

            await ingest.IngestAsync(doc.Id);   // re-ingest (edit/update path)
            var second = await db.KnowledgeChunks.AsNoTracking().Where(c => c.KnowledgeDocId == doc.Id)
                .Select(c => c.Id).OrderBy(x => x).ToListAsync();

            Assert.Equal(first, second);        // same ids upserted, no dupes
            Assert.NotEmpty(second);

            await ingest.PurgeAsync(doc.Id);
            var remaining = await db.KnowledgeChunks.AsNoTracking()
                .CountAsync(c => c.KnowledgeDocId == doc.Id);
            Assert.Equal(0, remaining);         // delete purges
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
            var doc = new KnowledgeDoc { Id = Guid.NewGuid(), BrandId = _fixture.BrandA,
                DocType = DocType.BrandPlaybook, Facet = KnowledgeFacet.Voice, Title = "Voice",
                Content = longProse, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
            db.KnowledgeDocs.Add(doc);
            await db.SaveChangesAsync();

            await ingest.IngestAsync(doc.Id);
            var many = await db.KnowledgeChunks.AsNoTracking().CountAsync(c => c.KnowledgeDocId == doc.Id);
            Assert.True(many > 1);

            doc.Content = "Now just one short section.";   // edit shorter → one chunk
            await db.SaveChangesAsync();
            await ingest.IngestAsync(doc.Id);              // re-ingest: higher-index chunks become orphans

            var few = await db.KnowledgeChunks.AsNoTracking().CountAsync(c => c.KnowledgeDocId == doc.Id);
            Assert.Equal(1, few);                           // orphans removed, no leftovers
        }
    }
}
```

- [ ] **Step 3: Run, expect FAIL.**

- [ ] **Step 4: Implement `KnowledgeIngestService`.** Chunk id is `DeterministicGuid.From(docId, chunkIndex)` so a re-ingest upserts the same ids (idempotent, like the MinIO/Meta keys). Embeds with `search_document:`. Promotes the doc's metadata onto each chunk.

```csharp
// backend/src/Infrastructure/Knowledge/KnowledgeIngestService.cs
using System.Text.Json;
using Backend.Core.Common;       // DeterministicGuid
using Backend.Core.Domain;
using Backend.Core.Knowledge;
using Backend.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace Backend.Infrastructure.Knowledge;

public sealed class KnowledgeIngestService : IKnowledgeIngestService
{
    private readonly AppDbContext _db;
    private readonly IKnowledgeChunker _chunker;
    private readonly IEmbeddingProvider _embeddings;

    public KnowledgeIngestService(AppDbContext db, IKnowledgeChunker chunker, IEmbeddingProvider embeddings)
    {
        _db = db; _chunker = chunker; _embeddings = embeddings;
    }

    public async Task IngestAsync(Guid docId, CancellationToken ct = default)
    {
        // RLS scopes this read to the bound brand.
        var doc = await _db.KnowledgeDocs.AsNoTracking().FirstOrDefaultAsync(d => d.Id == docId, ct)
            .ConfigureAwait(false);
        if (doc is null) return;   // not visible to this brand → no-op (degrade, don't crash)

        // True upsert keyed by deterministic chunk id. We do NOT RemoveRange(existing) then
        // Add(same id): EF Core identity resolution throws "another instance with the same key
        // value is already being tracked" when a Deleted entity and an Added entity share a PK
        // in one SaveChanges. Instead: load existing TRACKED, mutate-or-add per draft, then
        // remove any chunk no longer produced (shrink → no orphans). One SaveChanges inside the
        // BrandScope transaction (BeginAsync) = atomic.
        var existing = await _db.KnowledgeChunks
            .Where(c => c.KnowledgeDocId == docId)
            .ToDictionaryAsync(c => c.Id, ct).ConfigureAwait(false);

        var metadata = doc.Metadata is null
            ? null : JsonSerializer.Deserialize<KnowledgeChunkMetadata>(doc.Metadata);
        var drafts = _chunker.Chunk(doc.DocType, doc.Content, isCompetitor: metadata?.IsCompetitor == true);

        var keptIds = new HashSet<Guid>();
        foreach (var draft in drafts)
        {
            var id = DeterministicGuid.From(docId, draft.Index.ToString());
            keptIds.Add(id);
            var embedding = new Vector(
                await _embeddings.EmbedDocumentAsync(draft.Content, ct).ConfigureAwait(false));

            if (existing.TryGetValue(id, out var chunk))
            {
                chunk.Content = draft.Content;       // mutate the tracked row (no delete+add conflict)
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
            if (!keptIds.Contains(id)) _db.KnowledgeChunks.Remove(chunk);

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task PurgeAsync(Guid docId, CancellationToken ct = default)
    {
        var chunks = await _db.KnowledgeChunks.Where(c => c.KnowledgeDocId == docId).ToListAsync(ct)
            .ConfigureAwait(false);
        _db.KnowledgeChunks.RemoveRange(chunks);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
```

> Confirm the exact namespace of the existing `DeterministicGuid` helper (used by `MockMetaIntegration`/`StubOrchestrator`) and `using` it. If `From` only accepts `(Guid, string)`, pass `draft.Index.ToString()` as shown.

- [ ] **Step 5: Run the idempotency test, expect PASS.**

- [ ] **Step 6: Implement the Hangfire job** (mirror `ExecuteRunJob`: bind brand → `BeginAsync` → work → `CompleteAsync`).

```csharp
// backend/src/Infrastructure/Jobs/IngestKnowledgeDocJob.cs
using Backend.Core.Knowledge;
using Backend.Core.Multitenancy;

namespace Backend.Infrastructure.Jobs;

public sealed class IngestKnowledgeDocJob
{
    private readonly IBrandScope _scope;
    private readonly IBrandContext _brandContext;
    private readonly IKnowledgeIngestService _ingest;

    public IngestKnowledgeDocJob(IBrandScope scope, IBrandContext brandContext, IKnowledgeIngestService ingest)
    {
        _scope = scope; _brandContext = brandContext; _ingest = ingest;
    }

    // Ingest only. DELETE purges synchronously in the controller (no FK cascade, no orphan
    // window) — there is intentionally no async PurgeAsync job entrypoint.
    public async Task ExecuteAsync(Guid docId, Guid brandId, CancellationToken cancellationToken = default)
    {
        _brandContext.Bind(brandId);
        await using var handle = await _scope.BeginAsync(cancellationToken).ConfigureAwait(false);
        await _ingest.IngestAsync(docId, cancellationToken).ConfigureAwait(false);
        await handle.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
```

Register it: in `HangfireServiceCollectionExtensions.AddHangfireJobStore`, add `services.AddScoped<IngestKnowledgeDocJob>();`.

- [ ] **Step 7: Add the DTOs + validators** (follow `CreateBrandRequestValidator`).

```csharp
// backend/src/Api/Dtos/CreateKnowledgeDocRequest.cs
public sealed record CreateKnowledgeDocRequest(
    string Title, DocType DocType, KnowledgeFacet? Facet, string Content,
    string? Source, KnowledgeChunkMetadata? Metadata);
```

```csharp
// backend/src/Api/Dtos/CreateKnowledgeDocRequestValidator.cs
public sealed class CreateKnowledgeDocRequestValidator : AbstractValidator<CreateKnowledgeDocRequest>
{
    public CreateKnowledgeDocRequestValidator()
    {
        RuleFor(r => r.Title).NotEmpty().MaximumLength(200);
        RuleFor(r => r.Content).NotEmpty();
        RuleFor(r => r.DocType).IsInEnum();
        RuleFor(r => r.Facet).IsInEnum().When(r => r.Facet.HasValue);
        // Facet only meaningful for brand_playbook:
        RuleFor(r => r.Facet).Null()
            .When(r => r.DocType != DocType.BrandPlaybook)
            .WithMessage("Facet applies only to brand_playbook docs.");
    }
}
```

`UpdateKnowledgeDocRequest` mirrors create (same fields). Validators are auto-registered by the existing `AddValidatorsFromAssemblyContaining<CreateBrandRequestValidator>()`.

- [ ] **Step 8: Implement `KnowledgeController`.** Brand comes from `X-Brand-Id` via the existing `BrandContextMiddleware` + `IBrandContext` (same as `RunsController`). Create/Update persist the doc under brand scope then enqueue ingest; Delete enqueues purge.

```csharp
// backend/src/Api/Controllers/KnowledgeController.cs
[ApiController]
[Route("knowledge")]
public sealed class KnowledgeController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IBrandScope _scope;
    private readonly IBrandContext _brandContext;
    private readonly IBackgroundJobClient _jobs;
    private readonly IKnowledgeIngestService _ingest;

    public KnowledgeController(AppDbContext db, IBrandScope scope, IBrandContext brandContext,
        IBackgroundJobClient jobs, IKnowledgeIngestService ingest)
    { _db = db; _scope = scope; _brandContext = brandContext; _jobs = jobs; _ingest = ingest; }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(CreateKnowledgeDocRequest request, CancellationToken ct)
    {
        if (!_brandContext.HasBrand) return BadRequest(new { error = "X-Brand-Id header is required." });
        var brandId = _brandContext.RequireBrandId();

        var doc = new KnowledgeDoc
        {
            Id = Guid.NewGuid(), BrandId = brandId, Title = request.Title, DocType = request.DocType,
            Facet = request.Facet, Content = request.Content, Source = request.Source,
            Metadata = request.Metadata is null ? null : JsonSerializer.Serialize(request.Metadata),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };

        await using (var handle = await _scope.BeginAsync(ct))
        {
            _db.KnowledgeDocs.Add(doc);
            await _db.SaveChangesAsync(ct);
            await handle.CompleteAsync(ct);
        }

        _jobs.Enqueue<IngestKnowledgeDocJob>(j => j.ExecuteAsync(doc.Id, brandId, CancellationToken.None));
        return Accepted($"/knowledge/{doc.Id}", new { docId = doc.Id });
    }

    // PUT {id}: load the doc (RLS-scoped), mutate fields + UpdatedAt, save under brand scope,
    //           then _jobs.Enqueue<IngestKnowledgeDocJob>(j => j.ExecuteAsync(id, brandId, …)) — re-ingest replaces.

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!_brandContext.HasBrand) return BadRequest(new { error = "X-Brand-Id header is required." });
        await using var handle = await _scope.BeginAsync(ct);

        var doc = await _db.KnowledgeDocs.FirstOrDefaultAsync(d => d.Id == id, ct);   // RLS-scoped
        if (doc is null) { await handle.CompleteAsync(ct); return NotFound(); }

        // knowledge_chunks has NO FK / ON DELETE CASCADE (this repo avoids FKs — RLS is the
        // relationship). So purge chunks AND remove the doc in the SAME brand-scoped transaction:
        // synchronous + atomic, no orphan window and no permanent orphans on a failed job. Ingest
        // is async (it embeds — slow); purge is a fast delete, so it runs in-request.
        await _ingest.PurgeAsync(id, ct);
        _db.KnowledgeDocs.Remove(doc);
        await _db.SaveChangesAsync(ct);
        await handle.CompleteAsync(ct);
        return NoContent();
    }
}
```

> `Delete` is shown in full above (synchronous purge + doc removal in one transaction — there is no FK cascade). Implement `Update` per the inline comment: load the doc RLS-scoped, mutate fields + `UpdatedAt = now`, save under brand scope, then `Enqueue<IngestKnowledgeDocJob>(j => j.ExecuteAsync(id, brandId, CancellationToken.None))` so the re-ingest replaces the doc's chunks. Write the full body — do not abbreviate.

- [ ] **Step 9: Run, build + format gates.** `dotnet test backend/Backend.sln --filter "FullyQualifiedName~IngestIdempotencyTests"`, then build + format. Expected PASS.
- [ ] **Step 10: Commit.** `git commit -m "feat(rag): ingest service + Hangfire ingest job + KnowledgeController CRUD (idempotent upsert/purge)"`

---

## Task 6: Dense retrieval — IRetrievalService → PgVectorRetrieval (config-gated stage seam)

**Files:**
- Create: `backend/src/Core/Knowledge/IRetrievalService.cs`, `backend/src/Infrastructure/Knowledge/PgVectorRetrieval.cs`, `backend/src/Infrastructure/Configuration/Options/RetrievalOptions.cs`, `backend/src/Infrastructure/Knowledge/KnowledgeServiceCollectionExtensions.cs`
- Modify: `backend/src/Api/Program.cs`, `backend/src/Worker/Program.cs`, `backend/src/Infrastructure/Configuration/OptionsServiceCollectionExtensions.cs`, `backend/src/Api/HealthChecks/HealthCheckRegistration.cs`, `backend/src/Api/appsettings.json`
- Test: `backend/tests/IntegrationTests/Knowledge/EmptyRetrievalTests.cs`

- [ ] **Step 1: Define the retrieval surface.**

```csharp
// backend/src/Core/Knowledge/IRetrievalService.cs
using Backend.Core.Domain;
using Backend.Core.Orchestration.Contracts;   // ToolError

namespace Backend.Core.Knowledge;

public sealed record RetrievedChunk(
    Guid ChunkId, Guid DocId, string Content, DocType DocType, KnowledgeFacet? Facet, double Score);

public sealed record RetrievalResult(
    IReadOnlyList<RetrievedChunk> Chunks, bool Grounded, ToolError? Error = null);

public interface IRetrievalService
{
    /// <summary>Dense cosine top-k for the calling brand. Brand isolation is the RLS policy
    /// (via the bound BrandScope) — brandId is NOT used in a manual WHERE. docType is an
    /// explicit content filter. Empty recall ⇒ Grounded == false, never an exception (DL-022).</summary>
    Task<RetrievalResult> Retrieve(string query, Guid brandId, string? docType, int k);
}
```

- [ ] **Step 2: Define `RetrievalOptions`** (the stage seam — all config-bound, slice-3 stages off).

```csharp
// backend/src/Infrastructure/Configuration/Options/RetrievalOptions.cs
namespace Backend.Infrastructure.Configuration.Options;

public sealed class RetrievalOptions
{
    public const string SectionName = "Retrieval";

    // S0 (slice 3): query transform.
    public bool QueryTransformEnabled { get; init; }          // default off
    public int QueryVariants { get; init; } = 3;

    // S1 recall arms.
    public bool DenseEnabled { get; init; } = true;           // slice 2: the only wired arm
    public bool SparseEnabled { get; init; }                  // slice 3
    public int RecallDepth { get; init; } = 20;               // N

    // S2 (slice 3): rerank + blend.
    public bool RerankEnabled { get; init; }                  // slice 3
    public int FinalK { get; init; } = 5;                     // k
}
```

- [ ] **Step 3: Failing empty-retrieval test** (degrade, don't crash).

```csharp
[Trait("Category", "Isolation")]
public sealed class EmptyRetrievalTests : IClassFixture<KnowledgeFixture>
{
    private readonly KnowledgeFixture _fixture;
    public EmptyRetrievalTests(KnowledgeFixture f) => _fixture = f;

    [Fact]
    public async Task No_matching_corpus_returns_ungrounded_empty_no_throw()
    {
        var (db, scope, retrieval) = _fixture.CreateRetrieval(_fixture.BrandWithNoCorpus);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();
            var result = await retrieval.Retrieve("anything", _fixture.BrandWithNoCorpus, docType: null, k: 5);
            Assert.False(result.Grounded);
            Assert.Empty(result.Chunks);
            Assert.Null(result.Error);
        }
    }
}
```

- [ ] **Step 4: Run, expect FAIL.**

- [ ] **Step 5: Implement `PgVectorRetrieval`.** Dense-only now; the private stage methods + `RetrievalOptions` toggles are the seam slice 3 fills (sparse union, rerank, query-transform) **without restructuring**.

```csharp
// backend/src/Infrastructure/Knowledge/PgVectorRetrieval.cs
using Backend.Core.Domain;
using Backend.Core.Knowledge;
using Backend.Core.Orchestration.Contracts;
using Backend.Infrastructure.Configuration.Options;
using Backend.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Backend.Infrastructure.Knowledge;

public sealed class PgVectorRetrieval : IRetrievalService
{
    private readonly AppDbContext _db;
    private readonly IEmbeddingProvider _embeddings;
    private readonly RetrievalOptions _options;

    public PgVectorRetrieval(AppDbContext db, IEmbeddingProvider embeddings, IOptions<RetrievalOptions> options)
    { _db = db; _embeddings = embeddings; _options = options.Value; }

    public async Task<RetrievalResult> Retrieve(string query, Guid brandId, string? docType, int k)
    {
        var topK = k > 0 ? k : _options.FinalK;
        try
        {
            // S0 — query transform (slice 3). Off ⇒ single original query.
            var variants = _options.QueryTransformEnabled
                ? throw new NotSupportedException("S0 query transform is slice 3.")
                : new[] { query };

            // S1 — recall. Slice 2: dense arm only. Sparse union is slice 3 (toggle off).
            var candidates = await DenseRecallAsync(variants, docType, _options.RecallDepth)
                .ConfigureAwait(false);

            // S2 — rank. Slice 3 reranks; slice 2 keeps cosine order, takes top-k.
            var ranked = candidates.Take(topK).ToList();

            return new RetrievalResult(ranked, Grounded: ranked.Count > 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Provider/transport failure → structured ToolError, never an exception into the graph (DL-022).
            return new RetrievalResult([], Grounded: false,
                new ToolError("retrieval.failed", ex.Message, retryable: true));
        }
    }

    private async Task<List<RetrievedChunk>> DenseRecallAsync(
        IReadOnlyList<string> variants, string? docType, int n)
    {
        if (!_options.DenseEnabled) return [];
        DocType? typeFilter = docType is null ? null : Enum.Parse<DocType>(docType, ignoreCase: true);

        var merged = new Dictionary<Guid, RetrievedChunk>();
        foreach (var variant in variants)
        {
            var qVec = new Vector(await _embeddings.EmbedQueryAsync(variant).ConfigureAwait(false));

            // Brand scope = RLS (bound BrandScope). docType = explicit content filter. NEVER WHERE brand_id.
            IQueryable<KnowledgeChunk> q = _db.KnowledgeChunks.AsNoTracking();
            if (typeFilter is not null) q = q.Where(c => c.DocType == typeFilter);

            var hits = await q
                .Where(c => c.Embedding != null)
                .OrderBy(c => c.Embedding!.CosineDistance(qVec))
                .Take(n)
                .Select(c => new RetrievedChunk(
                    c.Id, c.KnowledgeDocId, c.Content, c.DocType, c.Facet,
                    1.0 - c.Embedding!.CosineDistance(qVec)))   // cosine similarity
                .ToListAsync()
                .ConfigureAwait(false);

            foreach (var hit in hits)
                if (!merged.ContainsKey(hit.ChunkId)) merged[hit.ChunkId] = hit;
        }
        return merged.Values.OrderByDescending(c => c.Score).ToList();
    }
}
```

> Reconcile `CosineDistance` against the Task 1 cheat-sheet. If projecting the distance twice is awkward to translate, order by distance and compute the score after materializing (read the `Vector` back and compute in-memory).

- [ ] **Step 6: Run the empty test, expect PASS.**

- [ ] **Step 7: Wire DI** in a new `KnowledgeServiceCollectionExtensions.AddKnowledge(config)`: the embeddings `Mode` switch (mirror `AddMetaIntegration`), the chunker, the ingest service, and retrieval. Register `RetrievalOptions` in `AddValidatedAppOptions`.

```csharp
// backend/src/Infrastructure/Knowledge/KnowledgeServiceCollectionExtensions.cs
public static class KnowledgeServiceCollectionExtensions
{
    public static IServiceCollection AddKnowledge(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IKnowledgeChunker, TypeDispatchedChunker>();
        services.AddScoped<IKnowledgeIngestService, KnowledgeIngestService>();
        services.AddScoped<IRetrievalService, PgVectorRetrieval>();

        var mode = (configuration["Embeddings:Mode"] ?? "nomic").Trim().ToLowerInvariant();
        if (mode == "mock")
        {
            services.AddSingleton<IEmbeddingProvider, DeterministicEmbeddingProvider>();
        }
        else
        {
            // host:port from config; app prepends the scheme (DL scaffold convention).
            var endpoint = configuration["Embeddings:Endpoint"] ?? "tei-embed:80";
            services.AddHttpClient<IEmbeddingProvider, NomicEmbeddingProvider>(c =>
                c.BaseAddress = new Uri($"http://{endpoint}"))
                .AddPolicyHandler(/* the repo's standard Polly timeout+retry */);
        }
        return services;
    }
}
```

Add to both `Api/Program.cs` and `Worker/Program.cs` (after `AddDataAccess()`): `builder.Services.AddKnowledge(builder.Configuration);`. Add `services.AddValidatedOptions<RetrievalOptions>(configuration, RetrievalOptions.SectionName);` in `OptionsServiceCollectionExtensions`.

- [ ] **Step 8: Gate the embeddings health check on Mode.** In `HealthCheckRegistration`, wrap the `.AddUrlGroup(embeddingsHealthUri, …)` so it registers only when `configuration["Embeddings:Mode"]` is not `"mock"` (mirrors the Vault feature-gate), so a mock-mode deployment never reports Unhealthy.

- [ ] **Step 9: appsettings.** Add `"Mode": "nomic"` to the `Embeddings` block and a `Retrieval` block with the defaults from `RetrievalOptions`.

- [ ] **Step 10: Build + format + run gates.** Expected PASS.
- [ ] **Step 11: Commit.** `git commit -m "feat(rag): PgVectorRetrieval dense cosine top-k behind config-gated stage seam + DI"`

---

## Task 7: Seed corpus (coffee roaster) + repeatable seeder + the KnowledgeFixture

**Files:**
- Create: `backend/src/Infrastructure/Knowledge/Seed/CoffeeRoasterCorpus.cs`, `backend/src/Infrastructure/Knowledge/Seed/KnowledgeSeeder.cs`, `backend/src/Api/Controllers/KnowledgeSeedController.cs`
- Create: `backend/tests/IntegrationTests/Knowledge/KnowledgeFixture.cs`

- [ ] **Step 1: Build the two-brand corpus data.** `CoffeeRoasterCorpus` returns a list of `(DocType, Facet?, Title, Content, KnowledgeChunkMetadata?)` per brand. Two brands so the leakage proof has something to isolate. Include, per brand:
  - `brand_playbook` docs, one per facet (`Voice`, `Persona`, `Mission`, `VisualStyle`) — section-aware prose.
  - a few `product` docs (whole-unit; metadata `product_id`/`price`/`category`).
  - a handful of voice-exemplar `historical_post` docs carrying **real, non-null** `EngagementRate` / `Ctr` / `AudienceSegment` (required for slice 3's rerank demo — seed them now).
  - some `platform_guidance` docs (soft heuristics only; `platform`/`surface` metadata).

- [ ] **Step 2: Implement the repeatable seeder.** Deterministic doc ids (`DeterministicGuid.From(brandId, title)`) so re-running replaces rather than duplicates; reuses `KnowledgeIngestService` so the seed path is the production path.

```csharp
// backend/src/Infrastructure/Knowledge/Seed/KnowledgeSeeder.cs
public sealed class KnowledgeSeeder
{
    private readonly AppDbContext _db;
    private readonly IBrandScope _scope;
    private readonly IBrandContext _brandContext;
    private readonly IKnowledgeIngestService _ingest;
    // ctor assigns all four

    public async Task SeedAsync(Guid brandId, CancellationToken ct = default)
    {
        _brandContext.Bind(brandId);
        await using var handle = await _scope.BeginAsync(ct);
        foreach (var spec in CoffeeRoasterCorpus.For(brandId))
        {
            var docId = DeterministicGuid.From(brandId, spec.Title);
            var existing = await _db.KnowledgeDocs.FirstOrDefaultAsync(d => d.Id == docId, ct);
            if (existing is null)
                _db.KnowledgeDocs.Add(spec.ToDoc(docId, brandId));
            else
                spec.Apply(existing);                 // update in place
            await _db.SaveChangesAsync(ct);
            await _ingest.IngestAsync(docId, ct);     // chunk + embed + upsert (idempotent)
        }
        await handle.CompleteAsync(ct);
    }
}
```

- [ ] **Step 3: Dev-only seed endpoint.** `KnowledgeSeedController` exposes `POST /dev/knowledge/seed` (brand from `X-Brand-Id`), registered/mapped only when `app.Environment.IsDevelopment()` (or guarded inside the action). Calls `KnowledgeSeeder.SeedAsync`. Register `KnowledgeSeeder` in `AddKnowledge`.

- [ ] **Step 4: Build the `KnowledgeFixture`** (the test harness all RAG integration tests share). Mirror `RlsLeakageFixture`: `pgvector/pgvector:pg16` container, `MigrateAsync`, two seeded brands + a third empty brand. Expose:
  - `BrandA`, `BrandB`, `BrandWithNoCorpus` ids.
  - `CreateAppContext()` (unscoped/superuser).
  - `CreateIngest(brandId)` → `(AppDbContext, IBrandScope, IKnowledgeIngestService)` using a `DeterministicEmbeddingProvider`.
  - `CreateRetrieval(brandId)` → `(AppDbContext, IBrandScope, IRetrievalService)` using the **same** `DeterministicEmbeddingProvider` instance, `RetrievalOptions` defaults.
  - In `InitializeAsync`, seed BrandA + BrandB via `KnowledgeSeeder` (mock embeddings) so corpus exists for the isolation/relevance tests.

```csharp
// shape of the brand-scoped helpers (mirror RlsLeakageFixture.CreateBrandScopedContext)
public (AppDbContext Db, IBrandScope Scope, IRetrievalService Retrieval) CreateRetrieval(Guid brandId)
{
    var db = CreateDbContext(AppUserConnectionString);
    var brandContext = new BrandContext();
    brandContext.Bind(brandId);
    var scope = new BrandScope(db, brandContext);
    var retrieval = new PgVectorRetrieval(db, _embeddings, Options.Create(new RetrievalOptions()));
    return (db, scope, retrieval);
}
```

- [ ] **Step 5: Build + format gates.** Expected PASS. (Tests that consume the fixture land in Task 8; a quick `dotnet build` confirms the fixture compiles.)
- [ ] **Step 6: Commit.** `git commit -m "feat(rag): coffee-roaster two-brand seed corpus + repeatable seeder + KnowledgeFixture"`

---

## Task 8: Adversarial proof tests — isolation + dense relevance (ship them)

The idempotency (Task 5), prefix (Task 3), and empty-degrade (Task 6) proofs already exist. This task adds the two headline proofs: **cross-brand leakage** and **dense relevance**.

**Files:**
- Create: `backend/tests/IntegrationTests/Knowledge/RagIsolationTests.cs`, `backend/tests/IntegrationTests/Knowledge/DenseRelevanceTests.cs`

- [ ] **Step 1: Cross-brand leakage test** — the bar. Brand A's `Retrieve` returns only brand A chunks; repeat bound to B.

```csharp
[Trait("Category", "Isolation")]
public sealed class RagIsolationTests : IClassFixture<KnowledgeFixture>
{
    private readonly KnowledgeFixture _fixture;
    public RagIsolationTests(KnowledgeFixture f) => _fixture = f;

    [Fact]
    public async Task Brand_A_retrieval_returns_zero_brand_B_chunks()
    {
        // Query with terms that exist in BOTH brands' corpora — only RLS keeps B out.
        var (db, scope, retrieval) = _fixture.CreateRetrieval(_fixture.BrandA);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();
            var result = await retrieval.Retrieve("brand voice and roast style", _fixture.BrandA, null, k: 20);
            Assert.NotEmpty(result.Chunks);

            await using var admin = _fixture.CreateAppContext();   // unscoped — admin path

            // Guard against a vacuous pass: BrandB really HAS chunks (it shares the same seed
            // corpus), so only RLS — not an empty corpus — keeps them out of A's result.
            Assert.True(await admin.KnowledgeChunks.AsNoTracking().CountAsync(c => c.BrandId == _fixture.BrandB) > 0);

            // Every returned chunk id must belong to BrandA (verified via the unscoped lookup).
            foreach (var hit in result.Chunks)
            {
                var owner = await admin.KnowledgeChunks.AsNoTracking()
                    .Where(c => c.Id == hit.ChunkId).Select(c => c.BrandId).FirstAsync();
                Assert.Equal(_fixture.BrandA, owner);   // zero B leakage on the dense arm
            }
        }
    }

    [Fact]
    public async Task Brand_B_retrieval_returns_zero_brand_A_chunks()
    {
        var (db, scope, retrieval) = _fixture.CreateRetrieval(_fixture.BrandB);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();
            var result = await retrieval.Retrieve("brand voice and roast style", _fixture.BrandB, null, k: 20);
            await using var admin = _fixture.CreateAppContext();
            foreach (var hit in result.Chunks)
            {
                var owner = await admin.KnowledgeChunks.AsNoTracking()
                    .Where(c => c.Id == hit.ChunkId).Select(c => c.BrandId).FirstAsync();
                Assert.Equal(_fixture.BrandB, owner);
            }
        }
    }
}
```

- [ ] **Step 2: Run, expect PASS.** Run: `dotnet test backend/Backend.sln --filter "Category=Isolation"`. Expected: all green (existing + new RAG leakage).

- [ ] **Step 3: Dense relevance test (mock-seeded).** A seeded query returns the right chunk. The deterministic mock's shared-vocabulary proximity makes the target chunk nearest.

```csharp
[Trait("Category", "Isolation")]
public sealed class DenseRelevanceTests : IClassFixture<KnowledgeFixture>
{
    private readonly KnowledgeFixture _fixture;
    public DenseRelevanceTests(KnowledgeFixture f) => _fixture = f;

    [Fact]
    public async Task Seeded_query_returns_the_expected_chunk_first()
    {
        var (db, scope, retrieval) = _fixture.CreateRetrieval(_fixture.BrandA);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();
            // Query reuses the distinctive vocabulary of a known product doc in BrandA's seed.
            var result = await retrieval.Retrieve(
                _fixture.BrandAProductQuery, _fixture.BrandA, docType: "product", k: 3);
            Assert.True(result.Grounded);
            Assert.Contains(_fixture.BrandAProductChunkId, result.Chunks.Select(c => c.ChunkId));
            Assert.Equal(_fixture.BrandAProductChunkId, result.Chunks[0].ChunkId);  // nearest
        }
    }
}
```

> `KnowledgeFixture` exposes `BrandAProductQuery` (text built from the target chunk's distinctive tokens) and `BrandAProductChunkId` (`DeterministicGuid.From(docId, "0")` for that whole-unit product doc).

- [ ] **Step 4: Opt-in real-TEI relevance** (local only, not CI). Add a second fact tagged `[Trait("Category","LiveEmbeddings")]` that, when `Embeddings:Mode=nomic` and `tei-embed` is reachable, seeds via `NomicEmbeddingProvider` and asserts the same nearest-chunk result. CI runs only `Category=Isolation` etc., never `LiveEmbeddings`.

```csharp
[Fact]
[Trait("Category", "LiveEmbeddings")]   // opt-in: requires a running tei-embed; excluded from CI
public async Task Real_tei_embed_returns_expected_chunk_first() { /* same shape, real provider */ }
```

- [ ] **Step 5: Run the mock relevance test, expect PASS.** Run: `dotnet test backend/Backend.sln --filter "FullyQualifiedName~DenseRelevanceTests&Category!=LiveEmbeddings"`.
- [ ] **Step 6: Build + format gates.** Expected PASS.
- [ ] **Step 7: Commit.** `git commit -m "test(rag): cross-brand leakage + dense relevance proofs (mock + opt-in live TEI)"`

---

## Task 9: Full verification + empty-volume migration proof + Output Summary

- [ ] **Step 1: Types.** Run: `dotnet build backend/Backend.sln -warnaserror`. Expected: PASS (nullable + analyzers clean).
- [ ] **Step 2: Format.** Run: `dotnet format backend/Backend.sln --verify-no-changes`. Expected: no changes.
- [ ] **Step 3: Full suite.** Run: `dotnet test backend/Backend.sln`. Expected: all green (existing + new RAG tests). `LiveEmbeddings` excluded by default.
- [ ] **Step 4: Mandatory isolation gate.** Run: `dotnet test backend/Backend.sln --filter "Category=Isolation"`. Expected: zero cross-brand leakage (existing two-brand suite **+** the RAG leakage tests).
- [ ] **Step 5: Empty-volume migration proof.** Run:

```bash
docker compose down -v
docker compose up -d postgres
dotnet ef database update -p backend/src/Infrastructure -s backend/src/Api \
  --connection "Host=localhost;Port=5432;Database=quorums;Username=postgres;Password=postgres"
```

Expected: InitialCreate **and** `KnowledgeVectorSchema` apply with no error; `\d knowledge_chunks` shows `embedding vector(768)`, `search_vector tsvector` (generated), the HNSW + GIN indexes, and the RLS policy.

- [ ] **Step 6: Secrets hygiene.** Run `gitleaks detect` (or the repo's pre-commit invocation). Expected: clean (no endpoint/token leaked).
- [ ] **Step 7: Optional full-stack smoke** (`Embeddings:Mode=nomic`): `docker compose up --build`; `POST /brands`; `POST /knowledge` (X-Brand-Id) → ingest job runs; `GET`/query via a temporary retrieval probe shows the brand's chunk; `POST /dev/knowledge/seed` populates the coffee-roaster corpus.
- [ ] **Step 8: Fill in the Output Summary**, then commit.

---

## Verification (end-to-end)

| Check | Command | Expected |
|---|---|---|
| Types | `dotnet build backend/Backend.sln -warnaserror` | green |
| Format | `dotnet format backend/Backend.sln --verify-no-changes` | no changes |
| Full tests | `dotnet test backend/Backend.sln` | all green |
| **Isolation (the bar)** | `dotnet test --filter "Category=Isolation"` | zero cross-brand leakage incl. RAG dense arm |
| Prefix | `dotnet test --filter "FullyQualifiedName~PrefixCorrectnessTests"` | `search_document:`/`search_query:` correct; mismatch caught |
| Idempotency | `dotnet test --filter "FullyQualifiedName~IngestIdempotencyTests"` | re-ingest replaces, no dupes; delete purges |
| Empty degrade | `dotnet test --filter "FullyQualifiedName~EmptyRetrievalTests"` | `Grounded==false`, no throw |
| Relevance | `dotnet test --filter "FullyQualifiedName~DenseRelevanceTests&Category!=LiveEmbeddings"` | target chunk nearest |
| Empty-volume migration | `docker compose down -v` → `ef database update` | applies clean; vector+tsvector+HNSW+GIN+RLS present |
| Secrets | `gitleaks detect` | clean |

---

## OUTPUT SUMMARY (fill in during/after implementation)

> A reviewer should read this section alone and know exactly what shipped and that the bar held.

**Slice: RAG ingest + dense retrieval (slice 2) — brand-isolated thin loop (date, branch `feat/rag-ingest-retrieval`, commits …)**

- **Schema:** `KnowledgeDoc`/`KnowledgeChunk` extended; one migration `KnowledgeVectorSchema` adds `embedding vector(768)`, a generated `search_vector tsvector` + GIN index, an HNSW `vector_cosine_ops` index, `DocType`/`Facet` enums, and `jsonb` metadata. RLS already covered both tables (InitialCreate) — no RLS SQL added. Applies clean from an empty volume.
- **Spike result (Task 1):** confirmed `Pgvector.EntityFrameworkCore <ver>` binds on EF 9.0.2; `UseVector()` wiring `<…>`; `CosineDistance` translation `<…>`; generated tsvector via raw SQL, unmapped, self-maintains.
- **Embedding:** `IEmbeddingProvider` with `NomicEmbeddingProvider` (HTTP→`tei-embed`, prefix split, dim guard) and `DeterministicEmbeddingProvider` (offline CI mock, hashed-token vectors). Gated by `Embeddings:Mode`.
- **Ingest:** type-dispatched chunker (whole-unit / section-aware window), `KnowledgeIngestService` (idempotent upsert keyed by `DeterministicGuid(docId, index)`), Hangfire `IngestKnowledgeDocJob`, `KnowledgeController` CRUD.
- **Retrieval:** `PgVectorRetrieval` dense cosine top-k behind the `RetrievalOptions` stage seam (sparse/rerank/query-transform present-but-off). Brand isolation = RLS; `docType` = explicit filter.
- **Seed:** two-brand coffee-roaster corpus, repeatable `KnowledgeSeeder`, with real non-null `engagement_rate`/`ctr`/`audience_segment` on historical posts (slice-3 rerank demo).
- **Proofs:** isolation (zero B leakage on the dense arm), prefix correctness, ingest idempotency, empty-degrade, dense relevance (mock + opt-in live TEI).
- **Gate evidence:** build `-warnaserror` clean; `format --verify-no-changes` clean; `dotnet test` N passed/0 failed; `Category=Isolation` green; empty-volume migration applies; `gitleaks` clean.

## Out of scope (slice 3+, do NOT build here)

Sparse FTS query, cross-encoder rerank (`tei-rerank`/`IRerankProvider`), query-transform (`IQueryTransformer`), the metadata blend, generation agents consuming RAG. The `tsvector` column + GIN index and the `RetrievalOptions` stage seam are left **ready, not wired**.
