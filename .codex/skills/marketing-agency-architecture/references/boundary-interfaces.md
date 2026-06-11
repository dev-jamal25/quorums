# Boundary Interfaces (the swappable spine)

Governs: DL-001, DL-004, DL-009, DL-010, DL-011, DL-016. Immutable input. Every
risky or external dependency sits behind a C# interface with a mock. **CI runs
entirely on mocks.**

## The six interfaces

| Interface | Implementations | Why it exists |
|-----------|-----------------|---------------|
| `IMetaIntegration` | `MockMetaIntegration`, `LiveMetaIntegration` (optional) | DL-004: demo never depends on Meta approval |
| `IMediaGenerationTool` | `GeminiMediaTool`, `MockMediaTool` | DL-001: Gemini is a tool, not the orchestrator; mock keeps CI free |
| `IStorageService` | `MinioStorage` (default), `LocalStorage` (tests) | DL-009: asset persistence, S3-portable |
| `IRetrievalService` | `PgVectorRetrieval` | DL-010: RAG grounding; brand-scoped by construction |
| `ISecretsProvider` | `VaultProvider`, `EnvProvider` (tests) | DL-011: KV + Transit access behind one seam |
| `IEmbeddingProvider` | `NomicEmbeddingProvider` (HTTP), mock (CI) | DL-016: embedding generation; self-hosted nomic-embed-text-v1.5 |

## Where they live

All interfaces are declared in `Core` (domain). All implementations live in
`Infrastructure`. Agent contracts also live in `Core`. The `Api` and `Worker`
projects depend on `Core` + `Infrastructure` and never reference an HTTP-client
detail directly — the orchestration layer is isolated from transport.

## DI registration (`Program.cs`)

Register with **explicit lifetimes**; **constructor injection only**:

- **Singleton** — clients and loaded models: the Anthropic/Claude client, the
  Gemini `HttpClient` wrapper, the MinIO client, the Vault client, the embedding
  HTTP client wrapper.
- **Scoped** — per-request services: `IBrandContext`, the `DbContext`, anything
  that must honor request-bound brand scope.
- **Transient** — everything else.

CI selects the mock implementations via configuration so no live key or network is
needed. A boundary interface + mock + registration template is in
`assets/boundary-interface.cs.template`.

## Per-interface contract notes

### `IMediaGenerationTool` (DL-001)

- Gemini is wrapped behind this typed interface with a mock implementation.
- Tool I/O validated with typed DTOs (the .NET equivalent of the Pydantic-validated
  tool I/O the standards call for).
- Provider keys come from settings (Vault KV → Options), **never inline**.
- The Media agent calls this; Claude orchestrates. Gemini never orchestrates.

### `IMetaIntegration` (DL-004, DL-005)

- One interface; `MockMetaIntegration` returns realistic publish/ads responses;
  `LiveMetaIntegration` is optional/bonus, selected via settings.
- Methods cover publish and the designed-for token refresh/revoke lifecycle.
- **Token decrypt happens only here, at call time** (see `isolation-and-secrets.md`).
- A full run completes with **zero live Meta calls** when the mock is selected.

### `IStorageService` (DL-009)

- `MinioStorage` (default) uses the Minio .NET SDK; keys are
  `brands/{brand_id}/assets/{asset_id}`, prefix derived from `IBrandContext`.
- `LocalStorage` for tests.
- Serves assets via presigned URLs (`GET /assets/{id}`).

### `IRetrievalService` (DL-010)

- `PgVectorRetrieval`; brand-scoped by construction (queries run under the RLS-bound
  `DbContext`).
- Backs caption/strategy grounding. See `rag-and-embeddings.md` for the ingest and
  query contract.

### `ISecretsProvider` (DL-011)

- `VaultProvider` (KV + Transit) / `EnvProvider` (tests).
- KV binds into the Options pattern; Transit key is `brand-tokens`.

### `IEmbeddingProvider` (DL-016)

- `NomicEmbeddingProvider` calls the local model server over HTTP; mock for CI.
- Embedding dim is config-bound and **must equal** the pgvector column dim
  (default 768). Enforces the `search_document:` / `search_query:` prefixes — see
  `rag-and-embeddings.md`.

## Structured tool errors

Tools return a structured `ToolError` into the agent loop on recoverable failure
(degrade, don't crash) rather than throwing. External calls get timeouts and
retries with backoff (transient only). This is a hard standard, not optional.
