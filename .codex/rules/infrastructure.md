---
paths:
  - "backend/src/Infrastructure/**/*.cs"
---

# Infrastructure & Data Access

<!-- Loads when Claude touches the Infrastructure project. Deepens the RLS/Vault/storage/embedding invariants in /CLAUDE.md. -->

## RLS is enforced by the connection, not by queries
- Brand scope is set by the `DbConnectionInterceptor` via transaction-scoped `set_config('app.current_brand', …, true)`. Every read/write goes through the brand-scoped `DbContext`.
- NEVER use `IgnoreQueryFilters()`, raw SQL, or a second unscoped context to "just get the data." Cross-brand access (admin, migrations) is a separate, explicitly-named path that gets reviewed.
- If a query needs a brand id in a `WHERE` clause to be correct, the isolation model is broken — fix the scope, not the query.

## EF Core / Npgsql
- Async only: `ToListAsync`, `SaveChangesAsync`, etc. No sync-over-async, no `.Result`/`.Wait()`.
- Reads are `AsNoTracking`. `CancellationToken` flows through every call.
- Map enums and value objects explicitly; no magic strings.

## Vault (`ISecretsProvider`)
- App config: Vault KV → strongly-typed Options at startup. Per-brand Meta tokens: Transit encrypt/decrypt only.
- Never persist a plaintext token, never write a decrypt to disk, never log token ciphertext or its Transit context.

## Storage (`IStorageService`)
- All blob I/O goes through the interface. No direct MinIO SDK calls outside Infrastructure.
- Keys are deterministic: `{brandId}/{assetId}` — this is what makes write retries idempotent.

## Embeddings (nomic-embed-text-v1.5, served via HF TEI — DL-024)
- Prefix `search_document:` on corpus, `search_query:` on queries — silent retrieval rot if mixed up. TEI does NOT apply these prefixes; the provider must.
- pgvector column dim MUST equal model output dim (768 default). Normalize vectors; cosine distance.
- `Embeddings__Endpoint` and `Reranker__Endpoint` are **host:port only** (e.g. `tei-embed:80`). The app prepends `http://`; never store a scheme in config.
- Gate `tei-embed` and `tei-rerank` health checks on whether the services are configured — absent config must not cause `/health` to report Unhealthy.

## External integrations
- Wrap every external call with a timeout + bounded retry (Polly). Return a typed result or `ToolError`; never let a raw `HttpRequestException` propagate to a caller.
