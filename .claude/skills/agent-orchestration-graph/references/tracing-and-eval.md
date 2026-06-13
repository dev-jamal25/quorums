# Tracing and Evaluation Hook

Encodes §10 of the frozen design.

## Langfuse tracing

- Emit a Langfuse span **per agent node** and **per tool call**.
- The assembled trace is the surface behind `GET /runs/{id}/trace` and the
  Phase 9 evaluation input.
- Span/trace ids live in `RunState.Trace` (`TraceRefs`) so they survive the
  pause/resume seam.

### Implemented contract (slice c2)

- `ITrace.RecordAsync(current, runId, brandId, node, tool?, status, startedAt,
  endedAt, errorMessage?)` records one completed span and returns the updated
  `TraceRefs` to thread back into `RunState`. The first span assigns the trace id,
  so ExecuteRun and ResumeRun share one continuous trace across the seam.
- `TraceRefs(TraceId, SpanIds, Spans)`: `TraceId` + `SpanIds` are the frozen
  Langfuse references; `Spans` (`TraceSpan { spanId, node, tool?, status,
  startedAt, endedAt, error? }`) is the assembled detail the endpoint returns —
  read straight from the checkpoint, so the trace is complete with or without a
  live Langfuse.
- **Optional, config-gated like Vault (DL-011 pattern).** `LangfuseTrace` is
  selected only when `Langfuse:BaseUrl` + `PublicKey` + `SecretKey` are all set
  (typed `HttpClient`, basic auth, 5s timeout, best-effort post). Otherwise
  `LocalTraceRecorder` assembles the trace in-process with no network. A Langfuse
  failure is logged and swallowed — **tracing never fails the run**. The Langfuse
  readiness check registers only when configured.
- `GET /runs/{id}/trace` loads the run under the **RLS-bound scope** (no unscoped
  read) and returns `{ runId, traceId, spans[] }`; a brand cannot read another
  brand's trace.

## Replayability (the reason the topology is a graph, not a swarm)

Runs are made replayable by:

- the fixed graph (deterministic control spine);
- recorded node inputs/outputs in `RunState`;
- controlled generation parameters where the provider allows.

## What Phase 9 evaluates against this trace

Tool-call correctness, caption quality, and brand consistency are evaluated
against this trace in Phase 9 (consolidation). Do not build the eval suite here —
only guarantee the trace exists and is complete enough to score.

**Success signal:** an identical run replays to an equivalent trace; spans exist
for every agent node and every tool call.
