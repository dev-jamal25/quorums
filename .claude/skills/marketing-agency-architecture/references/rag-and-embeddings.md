# RAG and Embeddings (DL-010, DL-016)

Governs the brand-knowledge RAG and the embedding runtime. Immutable input.

## RAG placement (DL-010)

- RAG is **MVP scope**, not optional.
- The corpus is a **per-brand, manager-editable knowledge base** (guidelines,
  products, voice exemplars, past content), CRUD'd through the CMS API, chunked and
  embedded into **pgvector in the same Postgres**, retrieved to ground
  strategy/caption generation.
- Same-Postgres pgvector means embeddings fall under the **existing RLS policies** —
  one isolation surface. **No external vector DB.**
- Mapped via `Pgvector.EntityFrameworkCore`. The retrieval interface is
  `IRetrievalService` → `PgVectorRetrieval`; queries run under the RLS-bound
  `DbContext` so they are brand-scoped by construction.
- Pattern reused from the prior Week-8 multi-tenant brand-knowledge CMS.

### Entities

- `KnowledgeDoc` (brand-scoped) — the manager-editable corpus document.
- `KnowledgeChunk` (+ vector column) (brand-scoped) — embedded grounding; RLS
  covers the vectors.

## Embedding runtime (DL-016)

- Model: **nomic-embed-text-v1.5**, self-hosted, open-source.
- Served over **HTTP** from `tei-embed` (`ghcr.io/huggingface/text-embeddings-inference:cpu-1.6`,
  `--model-id nomic-ai/nomic-embed-text-v1.5`) on port 80. DL-024 chose HF TEI over Ollama
  to co-host the cross-encoder reranker (DL-025: `tei-rerank`, `BAAI/bge-reranker-v2-m3`).
- Called from .NET via `IEmbeddingProvider` (`NomicEmbeddingProvider`, endpoint `tei-embed:80`),
  with a mock for CI. The model stays **out of the .NET process** (no ONNX/tokenizer wiring).
- Config key is `Embeddings__Endpoint=tei-embed:80` (host:port only; the app prepends `http://`).
  Reranker: `Reranker__Endpoint=tei-rerank:80` (same convention). **TEI does not apply task
  prefixes** — `NomicEmbeddingProvider` must prepend `search_document:` / `search_query:`.

### Know-your-model constraints (review gotchas — enforce all)

1. **Task prefixes are mandatory.**
   - Embed **corpus chunks** with the prefix `search_document:`.
   - Embed the **query** with the prefix `search_query:`.
   - Missing or mismatched prefixes **silently degrade** retrieval. Enforce the
     prefix inside `NomicEmbeddingProvider` so callers cannot forget it.
2. **Dimension match.** Native dimension is **768** (Matryoshka-truncatable to
   512/256/128/64). The **pgvector column dimension must equal the chosen output
   dim** (default **768**). Set the column dim in the **EF migration**. The
   `IEmbeddingProvider` output dim is config-bound and must equal the column dim.
3. **Normalize + cosine.** Normalize embeddings; index pgvector with cosine distance
   (`vector_cosine_ops`).

## Ingest pipeline

```
KnowledgeDoc (CMS write)
    → chunk
    → embed each chunk with prefix "search_document:"
    → store KnowledgeChunk rows (+ vector, brand-scoped) in pgvector
```

## Query path

```
user/agent query
    → embed query with prefix "search_query:"
    → cosine search over brand-scoped KnowledgeChunk vectors (RLS-bound DbContext)
    → return top-k chunks to ground generation
```

## Success signal

Captions for the demo brand **visibly use retrieved brand facts absent from the
prompt**; ingest embeds with `search_document:`, queries embed with
`search_query:`, retrieval returns brand-relevant chunks in the eval set; a
retrieval eval exists by Phase 9.

## Deferred (do not invent)

- **Chunking parameters, reranker runtime, and golden retrieval sets** are Phase 5/9
  decisions, not frozen here. The same local-server pattern can host a cross-encoder
  reranker via TEI if chosen in Phase 5. See `open-questions.md`.
