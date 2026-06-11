# Build Order — vertical slice first, depth in place (DL-013)

Governs the exact implementation sequence. Immutable input.

## The invariant

**From the end of Day 3 onward, the demo runs end-to-end every day.** Depth is added
in place; the slice is **never broken**. `docker compose up` + the demo script must
complete on any day from Day 3.

The eight-service topology and the slice are never sacrificed. Any reduction of
depth within a layer happens **only on the architect's explicit call** — never as an
automatic time-saving choice. The schedule is a conservative floor for a
full-throttle solo build and can be densified on request.

> Note from the architecture doc: this schedule does not yet carve out dedicated
> React/Next.js frontend-build time — flagged for re-pacing on request (see
> `open-questions.md`).

## Day-by-day

| Day | Goal |
|-----|------|
| **1** | Solution scaffold (`Api`/`Worker`/`Core`/`Infrastructure`), compose with all eight services up, Options + Vault KV wired, `GET /health` green, CI skeleton (`dotnet build`/`test`, `dotnet format`, analyzers, gitleaks). |
| **2** | EF Core model v1 + migration + **RLS policies (raw SQL) + the EF interceptor setting transaction-scoped `set_config`** (prove with a two-brand leakage test), Brand onboarding endpoint. |
| **3** | **Thin slice complete:** `POST /runs` → Hangfire `ExecuteRun` → one stub agent → mock media → MinIO write → checkpoint → approve in the dashboard → `ResumeRun` → mock publish → trace visible. |
| **4** | Real orchestration graph (Phase 2 output) replaces the stub; structured tool errors; run trace persisted properly. |
| **5** | RAG: bring up the embedding server (nomic-v1.5), knowledge CMS endpoints, ingest → chunk → embed (`search_document:`) → pgvector; queries embed with `search_query:`; agents ground on retrieval; Transit token flow for `BrandMetaConnection`. |
| **6** | Generation quality pass (Phase 5 outputs), Gemini live behind the tool interface, cost ceilings. |
| **7** | Eval suite (Phase 9 consolidation), CI gates on real thresholds, golden sets. |
| **8** | Demo script, README + architecture diagram, trace polish, buffer. |

## Why this order

The dominant risk is not scalability but Day 8 arriving with eight half-wired
services and no working loop. The slice converts that risk into "less depth
somewhere," which is survivable. **Defensibility:** "The demo has been runnable
since Day 3 — every day after that only added depth."

## Gate before proceeding past Day 2

The two-brand RLS leakage test (`scripts/RlsLeakageTests.cs` /
`scripts/run-rls-leakage-test.sh`) must pass before any feature work continues.
Isolation is not retrofitted.
