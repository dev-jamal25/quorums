---
name: generation-pipeline
description: Per-agent cognition for the Autonomous Digital Marketing Agency capstone — how each generation-pipeline agent turns a RunState slice plus RAG grounding into a typed output. Use whenever building or modifying the Content Strategist, Creative Director, or Copywriting agent prompts, the Supervisor selection call, the multi-angle (N=3) strategy flow, the structured-output schemas (ContentStrategy, SelectionDecision, CreativeDirection, MediaPromptBrief, Caption, Grounding) and their forced-tool enforcement, the per-run cost model (TokenBudget/MediaBudget, pre-Media gate, global ceiling, degrade path), or the PlatformConstraints validators (hashtag/caption/aspect-ratio limits). Trigger even when a request only mentions agent prompt, structured output, schema validation, retry loop, cost or budget gate, caption length, or aspect ratio for this capstone, and when replacing StubOrchestrator. Composes with marketing-agency-architecture, agent-orchestration-graph, brand-knowledge-rag, dotnet-engineering-standards.
---

# Generation Pipeline — agent cognition

This skill is the **cognition of each agent node**: how an agent turns its inputs
(a `RunState` slice + retrieved RAG grounding) into its **typed output**. It is
consumed by Claude Code build sessions — most importantly the build that
**replaces `StubOrchestrator`**, which composes this skill with
`agent-orchestration-graph` and `brand-knowledge-rag`.

## Re-decide nothing

Every choice here is frozen in `Agent_Orchestration_Design.md`
(**DL-017–023** for topology/state/failure/cost; **DL-027–030** for the
decisions this skill encodes). That document holds the rationale — point to it,
do not restate or reopen it. Frozen artifacts are append-only: supersede with a
new DL entry, never edit one in place.

## Scope boundary — read this before writing anything

**IN scope (this skill owns):** the three LLM agent prompts (Strategist,
Creative Director, Copywriting) + the Supervisor **selection** prompt; the
structured-output schemas + their enforcement; the cost model; the
`PlatformConstraints` validators.

**OUT of scope — do NOT redefine these; consume them:**

| Concern | Owned by | This skill's relation |
|---|---|---|
| Graph topology, routing, `RunState` shape, the human gate, the durable enqueue→resume seam | `agent-orchestration-graph` | plugs into the graph; never redefines it |
| RAG ingest + the retrieval pipeline | `brand-knowledge-rag` | consumes `IRetrievalService`; never implements retrieval |
| Gemini HTTP client / `IMediaGenerationTool` | architecture layer | defines the structured `mediaPromptBrief` and how the Media node *renders* it; not the client |
| The `StubOrchestrator` replacement | a downstream build prompt | supplies the agent implementations that build wires |
| Phase-9 eval / golden set / metrics | Phase 9 | must *emit* the cost-tracking + grounding provenance Phase 9 consumes |

If a request pushes you to change any OUT-of-scope item, stop and route it to the
owning skill — do not absorb it here.

---

## 1. Four prompt surfaces, one shared skeleton (DL-027)

Three LLM agents — **Content Strategist (Sonnet)**, **Creative Director
(Sonnet)**, **Copywriting (Haiku)** — plus the **Supervisor selection** call
(Sonnet). The **Media node is a Gemini executor with no Claude prompt**; its
instructions are the `mediaPromptBrief` authored by the Creative Director.

Every prompt is **instantiated from one 5-part skeleton** — do not bespoke each:

1. **role / mandate** — what the agent owns and must never touch.
2. **brand grounding block** — retrieved chunks, each tagged with its `docType`
   and a **provenance id**.
3. **input slice** — the upstream typed outputs it consumes from `RunState`.
4. **task + constraints** — incl. the `PlatformConstraints` relevant to this
   agent (§5).
5. **output-schema instruction** — the forced tool for its typed contract (§3).

Per-agent specifics — model, what it consumes from `RunState`, which `docType`s
it retrieves, and what it produces:

| Agent | Model | Consumes (RunState) | Retrieves (docType) | Produces |
|---|---|---|---|---|
| Content Strategist | Sonnet | `BrandProfile` + brief | `brand_playbook`(mission/persona), `historical_post`, `product`, `market_intel`, `platform_guidance` | `{ candidates: ContentStrategy[3] }` |
| Supervisor (selection) | Sonnet | 3 candidates + brief | — | `SelectionDecision` |
| Creative Director | Sonnet | chosen `ContentStrategy` | `brand_playbook`(visual_style), `product`, `platform_guidance` | `CreativeDirection` |
| Copywriting | Haiku | chosen `ContentStrategy` + `CreativeDirection` | `brand_playbook`(voice), `historical_post`, `product`, `platform_guidance` | `Caption` |
| Media | Gemini (executor) | `CreativeDirection.mediaPromptBrief` | — | `MediaAssetRef` (renders brief → Gemini prompt; no Claude call) |

Full per-agent prompt construction (role text, grounding injection format, input
slice, schema instruction) is in `references/agent-prompts.md`.

## 2. Hybrid Supervisor + multi-angle selection (DL-027)

The Supervisor is **two planes**:

- **Control plane = deterministic** (code, no LLM): budget check, `proceed` =
  default, `regenerate` triggered **only** by a failed validator / schema /
  constraint, gate routing. **Same inputs → same routing** (assert this).
- **Synthesis plane = one LLM call:** angle **selection**. This is the *only*
  LLM reasoning the Supervisor does.

**Multi-angle flow:** the Strategist emits **N = 3 candidate** `ContentStrategy`
options, each carrying its own one-line `angleRationale`. The Supervisor's
synthesis call returns `SelectionDecision { chosenIndex, rationale }`.
`RunState.Strategy` then holds the **single chosen** `ContentStrategy`; the three
candidates + the rationale **persist in the trace** (this is the "show the
Supervisor's decision" evidence, eval-able in Phase 9). The fan-out is contained
to the **Strategist → Supervisor** segment; Creative Director and Copywriting
consume only the chosen strategy.

## 3. Output schemas + enforcement (DL-028)

Field-level schemas (compact — full version with C# records in
`references/output-schemas.md`):

```
ContentStrategy   { pillar, angle, objective(enum), audience, angleRationale, calendarSlot?, grounding }
SelectionDecision { chosenIndex, rationale }
CreativeDirection { visualConcept, styleTokens[], colorTokens[], mediaPromptBrief, grounding }
MediaPromptBrief  { subject, style, composition, palette, mood, negative?, aspectRatio }
Caption           { hook, body, hashtags[], grounding }
Grounding         { grounded, chunkIdsUsed[], confidence(enum) }   // on every agent output
```

- `objective` is the fixed enum `{ awareness | engagement | conversion | traffic | retention }`.
- `pillar` is a free string **validated against the brand's playbook pillars at
  receipt** (DL-026). A miss is a **schema-level violation → regenerate**.
- `aspectRatio` in `MediaPromptBrief` is **set from `PlatformConstraints`** for
  the surface (§5), not chosen freely.

**Enforcement invariants — non-negotiable:**

- **Forced-tool / JSON-schema** output for **every** agent: the schema is the
  tool's input schema; deserialize `tool_use` input into the typed record.
- **C#-record-first:** the record is canonical; the JSON schema is **derived**
  from it. Never hand-maintain a second copy that can drift.
- **Validate on receipt → bounded retry (2) → `ToolError`.** The **only two**
  retry triggers are a **schema violation** and a **`PlatformConstraints`
  violation** (DL-027). Feed the *specific* validation error back into the retry
  prompt. On the second retry's failure, return `ToolError`; the Supervisor's
  deterministic control plane then degrades or fails per the DL-022/023 node
  policy (Strategist / Creative Director are fatal nodes).

## 4. Grounding provenance (DL-027)

The grounding block tags each retrieved chunk with a **provenance id**; agents
are instructed to ground in those chunks and to populate `chunkIdsUsed`. This is
what makes "captions visibly use retrieved brand facts" checkable and the
Phase-9 grounding eval scoreable. **Empty retrieval → `grounded = false`,
proceed ungrounded** (DL-022) — never a failure.

## 5. Cost model (DL-029) — summary

Full rules + the estimate table in `references/cost-model.md`. The shape:

- **Two dimensions:** `TokenBudget` (text agents, in tokens) and `MediaBudget`
  (images, count→$).
- **Two enforcement points, tracking everywhere else:** (a) a **pre-Media gate**
  — media affordable? proceed, else **degrade**; (b) a **global per-run dollar
  ceiling** — grossly exceeded → **fail** the run with a structured error.
- **Provisioning:** budget = **expected-case × 1.5**; the global hard ceiling
  sits at **worst-case** (N=3 candidates + 2 retries per retryable agent).
- **Prices are config-bound**, seeded with **current live values pulled at
  build/config time** — never hardcoded or recalled from memory. **Langfuse
  captures actuals**; Phase 9 refines the static estimates.
- **Degrade path:** media-budget breach → caption-only `ContentItemDraft` +
  trace note → human gate. Distinct from the global-ceiling breach, which
  **fails** the run. (Realizes the DL-023 pre-Media budget check.)

## 6. PlatformConstraints validators (DL-030) — summary

Full set + per-constraint remedy in `references/platform-constraints.md`. The
shape: a **global, deterministic platform config** (same for every tenant) —
**structural/format limits only**, distinct from the per-brand *soft*
`platform_guidance` RAG corpus. Defined here; the **publishing path reuses it**.
Content-policy compliance stays with the human gate, not here.

Frozen set (neither add nor remove):

| surface | constraint | limit | owner agent |
|---|---|---|---|
| `instagram_feed` | hashtagCount | ≤ 30 | Copywriting |
| `instagram_feed` | captionLength | ≤ 2200 chars | Copywriting |
| `instagram_feed` | aspectRatio | 4:5 or 1:1 | Creative Director (brief) |
| `instagram_reel` | aspectRatio | 9:16 | Creative Director (brief) |
| `instagram_story` | aspectRatio | 9:16 | Creative Director (brief) |

**Applied two ways:** *inform* (the relevant constraint is injected into the
agent's prompt, skeleton part 4) **and** *validate* (deterministic
post-generation check — **never trust the LLM to count**). A validation failure
feeds the §3 retry loop. **Remedy is per-constraint, declared in config**;
**aspectRatio is pre-enforced in the brief** before Gemini (no post-hoc remedy).
A **publish-time re-check** runs the same validator (defense-in-depth; catches
edits made at the human gate).

---

## Required proof tests — the skill is not done until these pass

A build session handed this skill must produce a system that passes all seven.
Encode them as the acceptance bar:

1. **Schema conformance** — each agent output validates; a malformed output
   triggers **exactly** the bounded retry (2), then `ToolError`.
2. **Multi-angle / selection** — Strategist returns 3 candidates; the Supervisor
   records `chosenIndex` + `rationale`; **all 3 persist in the trace**.
3. **pillar validation** — a pillar outside the brand's list triggers a
   **regenerate**.
4. **Grounding degrade** — empty retrieval → `grounded = false`, the agent
   **proceeds**.
5. **PlatformConstraints** — hashtags > 30 → **repair** (truncate + trace note);
   caption > 2200 → **regenerate** then truncate fallback; aspect ratio is **set
   in the brief**, not post-hoc.
6. **Cost degrade** — simulated media-budget breach → **caption-only draft** +
   trace note; global-ceiling breach → run **fails** with a structured error.
7. **Supervisor determinism** — control-plane routing is deterministic (same
   inputs → same routing); **only** the synthesis (selection) call is LLM.

## Definition of done

A Claude Code session handed this skill + a build prompt can implement the three
agents + the Supervisor selection + the schemas + the cost model + the
validators, plug them into the orchestration graph and RAG, **making none of
these decisions itself**, and the result passes the seven tests above.

## Reference files

- `references/agent-prompts.md` — per-agent prompt construction for the three
  agents + Supervisor selection (role, grounding injection, input slice, schema
  instruction) and the Media brief→Gemini rendering.
- `references/output-schemas.md` — field-level schemas as canonical C# records,
  forced-tool enforcement, the retry loop, record-first derivation.
- `references/cost-model.md` — budget dimensions, the two enforcement points,
  provisioning, prices-as-config, the degrade path, the per-call estimate table.
- `references/platform-constraints.md` — the constraint set, inform+validate,
  per-constraint remedy, the publish-time re-check.
