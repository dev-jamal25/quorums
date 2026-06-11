# RunState and Inter-Agent Contracts

Encodes DL-020 and §3/§4 of the frozen design. This is the contract layer —
build it first.

## RunState (§3)

A single typed object threaded through the graph and persisted as `RunCheckpoint`
(JSON) so it is serializable across the durable pause/resume seam. Agents
read/write only their declared slice; the **Supervisor is the only writer** of
`Phase`, `Draft`, approval routing, and `Budget`. No free-form blackboard.

```csharp
record RunState(
    Guid RunId,
    Guid BrandId,                       // RLS-scoped data, not a job payload
    GraphPhase Phase,                   // resume marker:
                                        // Strategy|Creative|Generation|Assembled|
                                        // AwaitingApproval|Publishing|Done
    ContentStrategy?   Strategy,        // Content Strategist
    CreativeDirection? Creative,        // Creative Director
    Caption?           Caption,         // Copywriting
    MediaAssetRef?     Media,           // Media Generation (MinIO key + Asset id)
    ContentItemDraft?  Draft,           // Supervisor (assembly join)
    ApprovalDecision?  Approval,        // set after the gate
    PublishResult?     Publish,         // Publishing
    Budget             Budget,          // TokenBudget/Spent, MediaBudget/Spent
    IReadOnlyList<ToolError> Errors,    // structured, append-only
    TraceRefs          Trace            // Langfuse trace/span ids
);
```

### GraphPhase — the single resume marker

`GraphPhase` is the **single** resume marker: `ResumeRun` reads it to re-enter the
graph at the correct node after the human wait. Values:
`Strategy | Creative | Generation | Assembled | AwaitingApproval | Publishing |
Done`.

### Writer rules

- **Supervisor only** writes `Phase`, `Draft`, approval routing, and `Budget`.
- Each agent writes only its declared slice (the column annotated in the record).
- `Errors` is structured and append-only.
- `BrandId` is RLS-scoped data carried in state — never passed as a job payload;
  jobs carry only `runId`.

## Typed inter-agent contracts (§4)

Each agent promises exactly one output slice and consumes only declared inputs.
Handoffs are typed, never free-form text.

- **Content Strategist** → `ContentStrategy { pillar, angle, objective, audience, (calendarSlot?) }`
- **Creative Director** → `CreativeDirection { visualConcept, styleTokens, colorTokens, mediaPromptBrief }`
- **Copywriting** → `Caption { hook, body, hashtags[] }`
- **Media Generation** → `MediaAssetRef { storageKey, modality, mimeType, assetId }`
- **Supervisor (assembly)** → `ContentItemDraft { captionRef, mediaRef, brandId, status }`
- **Publishing** → `PublishResult { externalRef?, status, error? }`

**Success signal:** `RunState` round-trips through Postgres and rehydrates
identically in a fresh process; every agent output is a typed record.
