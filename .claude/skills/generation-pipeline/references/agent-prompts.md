# Agent prompts — per-surface construction

> Frozen by DL-027. The four prompt surfaces are **instantiations of one
> 5-part skeleton**, not bespoke prompts. Build each by filling the five parts;
> never invent a sixth structure. The Media node has **no Claude prompt** — it is
> a Gemini executor that renders the Creative Director's `mediaPromptBrief`.

## Contents
- [The 5-part skeleton](#the-5-part-skeleton)
- [Grounding-block format](#grounding-block-format)
- [Content Strategist (Sonnet)](#content-strategist-sonnet)
- [Supervisor selection (Sonnet)](#supervisor-selection-sonnet)
- [Creative Director (Sonnet)](#creative-director-sonnet)
- [Copywriting (Haiku)](#copywriting-haiku)
- [Media node (Gemini executor)](#media-node-gemini-executor)

## The 5-part skeleton

Every LLM agent prompt (and the Supervisor selection call) is assembled from
these five parts in order:

1. **role / mandate** — one tight paragraph: what this agent **owns** and what it
   **must never touch**. The mandate is the boundary that keeps the responsibility
   matrix clean (Strategist = message, Creative Director = form, Copywriting =
   words). State the "never touch" explicitly — it is what stops slice bleed.
2. **brand grounding block** — the retrieved chunks for this agent's `docType`s,
   each tagged with its `docType` and a **provenance id** (see format below).
3. **input slice** — the upstream typed outputs this agent consumes from
   `RunState`, serialized as JSON. Pass *only* the declared slice — not the whole
   `RunState`.
4. **task + constraints** — the concrete instruction for this turn, plus the
   **relevant `PlatformConstraints`** injected verbatim (the *inform* half of
   DL-030). The agent is told the limit so it aims to comply; the deterministic
   validator still checks afterward (never trust the model to count).
5. **output-schema instruction** — bind the typed contract as a **forced tool**
   (the schema is the tool's input schema). Instruct the model to return *only*
   the tool call. Deserialize `tool_use.input` into the canonical C# record.

## Grounding-block format

Inject retrieved chunks so the agent can cite them and populate `chunkIdsUsed`.
Each chunk is tagged with its `docType` and a stable provenance id:

```
[grounding]
{chunkId=pb_mission_03 | docType=brand_playbook} <chunk text…>
{chunkId=hist_0412     | docType=historical_post} <chunk text…>
{chunkId=prod_017      | docType=product}         <chunk text…>
[/grounding]
```

Instruct every agent: "Ground your output in the chunks above. Put the ids of the
chunks you actually used in `grounding.chunkIdsUsed`, and set `grounding.confidence`
to reflect how well the chunks supported the output." **If the grounding block is
empty** (retrieval returned nothing), instruct the agent to set
`grounding.grounded = false` and **proceed ungrounded** (DL-022) — this is a
normal degraded path, not an error.

---

## Content Strategist (Sonnet)

- **Mandate:** owns *what to say* — the content **pillar**, marketing **angle**,
  and **objective**. Never touches visuals, copy wording, or hashtags.
- **Consumes (input slice):** `BrandProfile` + the campaign brief.
- **Retrieves (docType):** `brand_playbook` (mission/persona), `historical_post`,
  `product`, `market_intel`, `platform_guidance`.
- **Constraints injected:** no hard `PlatformConstraints` are owned by the
  Strategist (those belong to Copywriting and the Creative Director); the *soft*
  `platform_guidance` chunks arrive through the grounding block, not as a hard
  validator.
- **Produces:** `{ candidates: ContentStrategy[3] }` — **N = 3** distinct
  candidate strategies. Each candidate carries its own one-line `angleRationale`
  arguing why that angle fits the brand and brief. The three must be genuinely
  different angles, not paraphrases — the Supervisor's selection is only
  meaningful if the candidates diverge.
- **Schema instruction:** force the tool whose input schema is the 3-candidate
  envelope; `pillar` on each candidate must be one of the brand's playbook
  pillars (validated at receipt — a miss regenerates).

## Supervisor selection (Sonnet)

This is the **only** LLM call the Supervisor makes; everything else in the
Supervisor is deterministic code.

- **Mandate:** choose the single best candidate among the three for *this* brand
  and brief, and justify the choice in one line. Do not rewrite or merge
  candidates — select one index.
- **Consumes (input slice):** the 3 `ContentStrategy` candidates + the brief.
- **Retrieves:** nothing.
- **Produces:** `SelectionDecision { chosenIndex, rationale }`. `RunState.Strategy`
  is then set to `candidates[chosenIndex]`; the full candidate list + the
  rationale are written to the trace.
- **Schema instruction:** force the `SelectionDecision` tool; `chosenIndex` must
  be in `[0, 2]`.

## Creative Director (Sonnet)

- **Mandate:** owns *how it looks* — the visual concept, style/color tokens, and
  the structured `mediaPromptBrief`. Never decides the message (that is the chosen
  strategy) and never writes the caption.
- **Consumes (input slice):** the chosen `ContentStrategy`.
- **Retrieves (docType):** `brand_playbook` (visual_style), `product`,
  `platform_guidance`.
- **Constraints injected:** the **aspectRatio** rule for the target surface
  (DL-030). The Creative Director **sets** `mediaPromptBrief.aspectRatio` to a
  value permitted for the surface — this is the *pre-enforcement* of aspect ratio;
  there is no post-hoc aspect-ratio remedy.
- **Produces:** `CreativeDirection { visualConcept, styleTokens[], colorTokens[],
  mediaPromptBrief, grounding }`. The `mediaPromptBrief` is the **only** instruction
  the Media node receives.
- **Schema instruction:** force the `CreativeDirection` tool; `mediaPromptBrief`
  is itself a structured object (not a free-text prompt) — see
  `output-schemas.md`.

## Copywriting (Haiku)

- **Mandate:** owns the **caption** — hook, body, hashtags. Never changes the
  strategy or the visual direction.
- **Consumes (input slice):** the chosen `ContentStrategy` + the
  `CreativeDirection`.
- **Retrieves (docType):** `brand_playbook` (voice), `historical_post`,
  `product`, `platform_guidance`.
- **Constraints injected:** **hashtagCount** (≤ 30) and **captionLength**
  (≤ 2200 chars) for the surface (DL-030), injected verbatim so the model aims to
  comply. The deterministic validator still checks after generation: over-hashtag
  → repair (truncate + trace note); over-length → regenerate with a hard-truncate
  fallback.
- **Produces:** `Caption { hook, body, hashtags[], grounding }`.
- **Schema instruction:** force the `Caption` tool; `hashtags` `maxItems` is bound
  from the surface's `PlatformConstraints`.

## Media node (Gemini executor)

- **No Claude prompt.** The node takes `CreativeDirection.mediaPromptBrief` and
  **renders** it into the Gemini image prompt — flatten the structured brief
  (`subject`, `style`, `composition`, `palette`, `mood`, optional `negative`) into
  the provider's prompt format, and pass `aspectRatio` through to the generation
  parameters. The brief's `aspectRatio` was already constrained by the Creative
  Director, so the Media node does not re-decide it.
- **Produces:** `MediaAssetRef` (MinIO `storageKey` + `assetId` + metadata).
- **Boundary:** the Gemini HTTP client and `IMediaGenerationTool` live in the
  architecture layer — this skill defines only the brief and the rendering, not
  the client.
