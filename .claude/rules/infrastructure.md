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
- **Carve-out — read-only FTS over the generated `tsvector`.** The brand-knowledge sparse-retrieval arm (`PgVectorRetrieval`) MAY use `FromSqlRaw` / `SqlQueryRaw` **only** to read the unmapped, generated `search_vector` column (which EF/LINQ cannot express), and **only on the brand-scoped `DbContext` connection** so the session-scoped `set_config('app.current_brand', …)` RLS policy still applies. It is read-only, NEVER sets a brand id in a `WHERE`, and is proven leak-free by the sparse-arm isolation test (`Category=Isolation`). Any other raw SQL remains banned.

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

## Embeddings (nomic-embed-text-v1.5)
- Prefix `search_document:` on corpus, `search_query:` on queries — silent retrieval rot if mixed up.
- pgvector column dim MUST equal model output dim (768 default). Normalize vectors; cosine distance.

## Claude / LLM calls (`Microsoft.Extensions.AI` `IChatClient`)
- Every Claude call goes through `Microsoft.Extensions.AI`'s `IChatClient` (already a transitive dep of `Microsoft.Agents.AI.Workflows`). The Anthropic client (`Anthropic.SDK` — the community package; there is no official .NET SDK) is registered as an `IChatClient` **once, here in Infrastructure**: today `AddSingleton<IChatClient>(sp => new AnthropicClient(apiKey).Messages)` (`AnthropicClient.Messages` *is* an `IChatClient`); key from `AnthropicOptions`, model id config-bound per call (`ChatOptions.ModelId`).
- The `Anthropic.SDK` type **never leaves Infrastructure**; consumers inject `IChatClient` only (see `orchestration.md`). NEVER add a second Claude-call path (no bespoke `HttpClient`, no direct SDK calls from feature/agent/Worker code).

## External integrations
- Wrap every external call with a timeout + bounded retry (Polly). Return a typed result or `ToolError`; never let a raw `HttpRequestException` propagate to a caller.
