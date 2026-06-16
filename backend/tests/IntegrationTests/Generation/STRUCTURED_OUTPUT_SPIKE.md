# STEP A — Structured-output spike result (DL-034 R4)

Decides the generation-pipeline structured-output seam. One real call through the registered
`IChatClient` (`Anthropic.SDK` 5.4.3 `AnthropicClient.Messages`, the same adapter wired in
`KnowledgeServiceCollectionExtensions`). Harness: `ForcedToolSpikeTests` (Trait
`Category=Spike`, gated on a live key, excluded from CI).

## Outcome: **forced tool_choice FORWARDS** → proceed on R4 path (a) via `IChatClient`.

### Probe A — invokable forced `AIFunction` + `ChatToolMode.RequireSpecific` (decisive)

Schema **generated from the record** (`AIJsonUtilities.CreateJsonSchema(typeof(SpikeAnswer))`,
where `record SpikeAnswer(string City, int ConfidencePercent)`), attached to an **invokable**
`AIFunction` subclass, forced via `RequireSpecific("record_answer")`.

**OUTGOING request body** (captured off the wire):

```json
{
  "max_tokens": 512, "stream": false, "model": "claude-haiku-4-5",
  "messages": [{"role":"user","content":[{"type":"text","text":"What is the capital of France? Reply conversationally in a full sentence."}]}],
  "tools": [{
    "name": "record_answer",
    "description": "Record the answer as structured fields.",
    "input_schema": {"type":"object","required":["city","confidencePercent"],
                     "properties":{"city":{"type":"string"},"confidencePercent":{"type":"integer"}}}
  }],
  "tool_choice": {"type":"tool","name":"record_answer"}
}
```

- `tool_choice:{type:"tool",name:"record_answer"}` is present → **the forced tool forwards**.
- The record-derived `input_schema` forwards intact (no hand-maintained dual).
- A prose-warranting prompt produced **no text** — the model emitted only the tool call.

**INCOMING** `tool_use` input deserialized into the record:

```json
{ "City": "Paris", "ConfidencePercent": 100 }
```

### Probe B — `GetResponseAsync<T>` (the MEAI structured-output helper) — NOT usable here

With the same record, the helper sent **no `tools` and no `tool_choice`** (plain message only)
and returned no parsed result. So the convenience helper does **not** force a tool through this
adapter — it cannot be the seam.

## Decisions for STEP C (frozen by this spike)

1. **Seam = invokable `AIFunction` + `RequireSpecific`**, read `FunctionCallContent` from the
   response, deserialize `Arguments` → record. (R4 path (a).)
2. **The tool must be an *invokable* `AIFunction`** — `AIFunctionFactory.CreateDeclaration(...)`
   (declaration-only) is dropped by the adapter and yields a 400
   `"Tool 'record_answer' not found in provided tools"`. Use a thin `AIFunction` subclass whose
   `JsonSchema` is the record-derived schema and whose `InvokeCoreAsync` throws (never invoked).
3. **Schema is generated from the record** via `AIJsonUtilities.CreateJsonSchema(typeof(T))`
   (experimental `MEAI001`; pragma-scoped). Confirmed it serialises correctly on the wire.
4. A forwarded forced `tool_choice` guarantees only a tool_use with schema-**guided** input —
   Anthropic does not hard-validate the schema — so the 2-retry loop + field validators are
   load-bearing regardless.
