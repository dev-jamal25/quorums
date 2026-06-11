# Agent Roster and Responsibility Matrix

Encodes DL-019 and §2 of the frozen design. Seven agents in the topology: **five
active on the MVP critical path** plus the **Supervisor**; **two (Ads
Optimization, Analytics) wired in as designed-for stubs** — present in the graph,
not exercised in the MVP loop, **not cut**.

## Responsibility matrix

| Agent | Owns | Key tool(s) | Consumes | Produces |
|---|---|---|---|---|
| **Supervisor** (orchestrator) | Node sequencing, bounded decisions (proceed/regenerate, idea selection), budget checks, gate trigger, failure adjudication | none external (Claude reasoning over `RunState`) | `RunState` | routing + `RunState` mutations |
| **Content Strategist** | *What to say*: content pillar, marketing angle, objective; (designed-for) calendar slot | `IRetrievalService` (brand-knowledge RAG) | `BrandProfile`, brief | `ContentStrategy` |
| **Creative Director** | *How it looks*: visual concept, style/color tokens, media-prompt brief | `IRetrievalService` (visual exemplars) | `BrandProfile`, `ContentStrategy` | `CreativeDirection` |
| **Copywriting** | Caption, hook, hashtags | `IRetrievalService` (voice exemplars) | `ContentStrategy`, `CreativeDirection`, `BrandProfile` | `Caption` |
| **Media Generation** | Media asset (image + video) | `IMediaGenerationTool` (Gemini, modality param), `IStorageService` (MinIO) | `CreativeDirection` (media-prompt brief) | `MediaAssetRef` (MinIO key + metadata) |
| **Publishing** | Translating an *approved* `ContentItem` into Meta publish action(s) + publish-failure handling | `IMetaIntegration` (mock\|live) | approved `ContentItem`, `BrandMetaConnection` (token via Vault Transit) | `PublishResult` |
| **Ads Optimization** *(stub)* | Boost/campaign optimization, budget recommendations | `IMetaIntegration` (ads tools) | — | — *(advanced)* |
| **Analytics** *(stub)* | KPI tracking, performance scoring, feedback into next plan | — | — | — *(advanced)* |

## Granularity defenses (the "why so many agents" answers)

- **Content Strategist vs. Creative Director** — clean line of *content* vs.
  *form*. The Strategist owns the marketing message/angle and never touches
  visuals; the Creative Director owns the visual concept and media-prompt brief
  and never decides the message. They share `BrandProfile` as input but write
  **disjoint** `RunState` slices.
- **Publishing earns "agent" status** via a genuine decision: assembling the
  approved item into the correct Meta MCP tool sequence/payload and handling
  publish failure (retry vs. surface-for-human). In the live path this is real
  multi-tool orchestration; in mock it is exercised through the same interface.
  The failure-handling must actually exist, or it degrades to a pass-through
  executor — which is not allowed.

## Media modality note

`IMediaGenerationTool` covers image and video; modality is a **tool parameter**,
not a topology branch. Per DL-003, image is the MVP critical path and video is
advanced scope behind its own higher cost ceiling — "same loop, more expensive
tool, same interface." The graph is unchanged by modality.

## Retrieval binding

Retrieval-using agents (Content Strategist, Creative Director, Copywriting) call
`IRetrievalService` under the RLS-bound DbContext; queries embed with the
`search_query:` prefix (DL-016).

## Stub rules (DL-019)

- Stubs implement the node interface and return a **not-implemented marker**.
- Stubs are present in the graph wiring.
- Stubs are required advanced scope kept so the MVP five-agent path stays the
  deliverable floor. They are stubbed, not invented, not cut.

**Success signal:** the responsibility matrix shows no overlapping ownership; the
two planning agents write disjoint `RunState` slices.
