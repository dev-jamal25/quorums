# Tracing and Evaluation Hook

Encodes §10 of the frozen design.

## Langfuse tracing

- Emit a Langfuse span **per agent node** and **per tool call**.
- The assembled trace is the surface behind `GET /runs/{id}/trace` and the
  Phase 9 evaluation input.
- Span/trace ids live in `RunState.Trace` (`TraceRefs`) so they survive the
  pause/resume seam.

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
