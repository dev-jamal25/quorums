# Interfaces and schema

The .NET contract surface and the EF Core schema. Frozen by **DL-025** (interfaces),
**DL-026** (entities/columns), **DL-016 / DL-024** (embedding dim + runtime), and
**DL-010 / DL-002** (RLS isolation). Idioms follow `dotnet-engineering-standards`;
this file states only what is RAG-specific. Implement, do not re-decide.

## 1. Interfaces

All four are registered in DI (`Program.cs`) with explicit lifetimes and injected by
constructor. **CI runs entirely on the mocks** — no network model server in tests.

### `IRetrievalService` → `PgVectorRetrieval`
The only public retrieval surface. The four stages (S0–S2) are **private** to the
implementation.

```csharp
public interface IRetrievalService
{
    Task<RetrievalResult> Retrieve(string query, Guid brandId, DocType? docType, int k);
}
```

- `RetrievalResult` carries ranked chunks **and** a `grounded` bool (false on empty
  recall — degrade, don't crash).
- Runs under the RLS-bound `DbContext`; brand scope is the policy, not an argument
  used in a manual `WHERE`.
- Owns the S2 metadata **blend** (the providers do not).
- **`docType` is a typed `DocType?` (DL-033), never a string.** A caller cannot pass an
  unparseable or mis-cased value; `null` = all doc types the caller may read. The dense filter
  is `c.DocType == value` (EF translates it through the existing converter); the sparse
  (raw-SQL) arm binds the converter's stored string as a parameter — never a hand-written
  literal, never `Enum.Parse`.

> **DL-033 — `docType` is typed, and stored `doc_type` is PascalCase.** The column persists
> the **default enum member-name conversion** (`HasConversion<string>()`): `BrandPlaybook`,
> `HistoricalPost`, `Product`, `MarketIntel`, `PlatformGuidance` — **not** the DL-026
> snake_case taxonomy (`brand_playbook`, …). Snake_case is the **conceptual / HTTP-boundary**
> spelling only; the stored representation is PascalCase and is **not** changed by DL-033 (a
> pure contract-shape tighten — no runtime/schema change). **⚠ Never filter `doc_type` with a
> snake_case literal in raw SQL — it matches nothing.**

### `IEmbeddingProvider` → `NomicEmbeddingProvider` (HTTP → TEI) + CI mock
```csharp
public interface IEmbeddingProvider
{
    Task<float[]> EmbedDocument(string text);   // applies "search_document:"
    Task<float[]> EmbedQuery(string text);      // applies "search_query:"
}
```

- Two methods, **not one with a flag** — the prefix is the contract, and splitting
  the methods makes a prefix mismatch a compile-time-obvious mistake rather than a
  silent argument error.
- Output dim is **config-bound and must equal the pgvector column dim (768)**.
- HTTP target is the `tei-embed` container; the app prepends the scheme to a
  `host:port` config value. Config-gated; a missing/disabled server must not crash
  startup — it surfaces as a `ToolError` at call time.
- The **CI mock** returns deterministic vectors (e.g. seeded by text hash) so tests
  are reproducible and offline.

### `IRerankProvider` → `CrossEncoderRerankProvider` (HTTP → TEI) + CI mock
```csharp
public interface IRerankProvider
{
    Task<IReadOnlyList<RerankScore>> Rerank(string query, IReadOnlyList<string> docs);
}
```

- Returns **pure cross-encoder relevance** (`RerankScore { index, relevance }`).
  **No metadata blend here** — the blend is `PgVectorRetrieval`'s job. This boundary
  is load-bearing for the Phase-9 ablation.
- HTTP target is the `tei-rerank` container (`bge-reranker-v2-m3`, `/rerank`).
  Config-gated; deterministic CI mock.

### `IQueryTransformer` → multi-query expander (Haiku) + CI mock
```csharp
public interface IQueryTransformer
{
    Task<IReadOnlyList<string>> Expand(string query, int variants);   // default 3
}
```

- Backed by Haiku in production; **CI mock** returns deterministic variants.
- Config-gated (S0 toggle); off → the pipeline runs on the single original query.

## 2. EF entities

Both brand-scoped, both RLS-covered. The chunk is the embedded + searchable unit.

### `KnowledgeDoc`
```
KnowledgeDoc
  Id            (PK)
  BrandId       (RLS)            // FK to Brand; the scope
  DocType       (enum)           // brand_playbook | historical_post | product
                                 //  | market_intel | platform_guidance
  Title / Source
  RawContent                     // pre-chunk source
  StructuredFields (JSON)        // performance, price, etc. — promoted to chunk metadata at ingest
  CreatedAt / UpdatedAt
```

### `KnowledgeChunk`
```
KnowledgeChunk
  Id            (PK)             // ingest upsert key (idempotency)
  DocId         (FK → KnowledgeDoc)
  BrandId       (RLS)            // denormalized for the RLS policy on the chunk table
  DocType       (enum)           // copied from doc; drives the retrieval pre-filter
  Facet         (enum?, null)    // brand_playbook only: voice|persona|mission|visual_style
  Content       (text)           // the chunk text — clean, NO metadata concatenated in
  Embedding     (vector(768))    // pgvector; dim == model output dim
  SearchVector  (tsvector)       // sparse arm; GIN-indexed
  Metadata      (jsonb)          // engagement_rate, ctr, audience_segment, objective,
                                 //  date, product_id, price, category, source,
                                 //  is_competitor, platform, surface — structured, never embedded
  CreatedAt
```

- `Embedding` mapped via `Pgvector.EntityFrameworkCore`.
- `SearchVector` is a `tsvector`; populate it from `Content` (generated column or
  ingest-time write) and **GIN-index** it.
- `Metadata` is `jsonb`; the S1 filter and S2 blend read it structurally.

## 3. Migration — one migration, ships RLS + both indexes

The schema change is a single EF migration that **applies cleanly from an empty
volume** and includes, via `migrationBuilder.Sql(...)` where EF can't express it:

1. **The pgvector / tsvector columns** (`vector(768)`, `tsvector`).
2. **RLS policy on both `KnowledgeDoc` and `KnowledgeChunk`:**
   `ALTER TABLE … ENABLE ROW LEVEL SECURITY;`
   `CREATE POLICY … USING (brand_id = current_setting('app.current_brand')::uuid);`
   — identical Postgres to every other brand-scoped table; only the EF interceptor
   that sets the session var is .NET-specific. **No `FORCE`-free gaps** — the policy
   must cover the chunk table, since that is what retrieval queries.
3. **HNSW index** on `Embedding` with `vector_cosine_ops` (dense arm).
4. **GIN index** on `SearchVector` (sparse arm).

> Isolation is **versioned schema**, not a manual step. A brand-scoped table without
> an RLS policy in its migration is a defect, not a shortcut.

## 4. DI registration (lifetimes)

- `PgVectorRetrieval` — **Scoped** (per-request, uses the RLS-bound DbContext).
- `NomicEmbeddingProvider`, `CrossEncoderRerankProvider` — clients are **Singleton**
  (`IHttpClientFactory`-backed); the providers themselves can be Singleton.
- `IQueryTransformer` — Singleton (stateless Haiku client) or Scoped; either is fine.
- All four resolve to **mocks in the CI/test composition root**.

## 5. What this skill does NOT define

- Per-agent prompts / structured-output schemas → `generation-pipeline`.
- Golden sets / eval metrics → `evaluation-and-ci-gates` (this skill only guarantees
  the toggleability they depend on).
- `PlatformConstraints` hard-rule validators → generation/publishing path, not RAG.
