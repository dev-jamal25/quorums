## Implementation resolutions — Phase 5 generation pipeline (frozen, DL-034)

R1 · Budget gate placement + asymmetric join (refines DL-029/023/021). The pre-Media
budget check is the Media node's FIRST action. On media-budget breach: emit a
media-skipped result (MediaAssetRef = null + a BudgetDegraded trace event), make ZERO
Gemini calls, return normally (not a ToolError). The fan-in barrier treats a null
MediaAssetRef as a valid caption-only result; assembly yields a caption-only
ContentItemDraft. Copywriting is independent and always completes.

R2 · Global-ceiling = fork-time snapshot (accepted bound). The Media node's
global-dollar check reads Budget.Spent at fork time, before Copywriting's parallel
token spend is reconciled at the join. Media dimension is exact; the global cap can be
overshot by at most one media call when both branches commit in the same window.
Bounded, documented - an in-flight parallel cost can't be reconciled before it finishes.

R3 · Budget single-writer (refines DL-020). Only the Supervisor writes RunState.Budget,
reconciled at the join via slice-1's existing fan-in merge. Parallel nodes RETURN their
incurred cost in their output; the Media node only READS for its gate. No second writer;
do not invent a new merge.

R4 · Structured-output seam + fallback hierarchy (refines DL-028/032). Schema is
GENERATED from the canonical C# record (JsonSchemaExporter on .NET 10, or Extensions.AI
AIJsonUtilities) - never a hand-maintained dual. Enforcement, decided once from the
spike, applied to all five Claude calls: (a) forced tool-choice via IChatClient if the
adapter forwards tool_choice:{type:tool,name}; else (b) a thin Infrastructure
structured-output seam over the SDK's native forced-tool (SDK type stays inside
Infrastructure per DL-032); (c) schema-in-prompt + parse only as last resort. A forced
tool_choice yields a tool_use with schema-GUIDED input, not API-validated fields - so
the 2-retry and every field validator below stay load-bearing regardless.

R5 · Field validators (load-bearing, post-deserialization). Run after deserialization;
forced-tool does not cover them. pillar in the structured pillar list (R7) else
regenerate; SelectionDecision.chosenIndex in [0, N) else schema-violation -> retry ->
ToolError; grounding per R6.

R6 · Grounding honesty (refines DL-028). Intersect the model's claimed chunkIdsUsed
with the provenance ids actually injected into that prompt; keep the intersection.
Derive grounded = (intersection non-empty) - do NOT trust the model's self-reported
grounded boolean. confidence may be model-set, reported alongside the validated id count.

R7 · pillar contract + data (repo-check first). Check whether BrandProfile (or a
brand-scoped record) already exposes a structured pillar list. Yes -> validate against
it. No -> add ContentPillars: string[], brand-scoped/RLS like every tenant field, set at
onboarding and seeded for the coffee-roaster demo (ships a new RLS-covered migration;
Category=Isolation must cover it). The structured list is the validation contract; the
brand_playbook prose stays generation grounding - not redundant. If deferred, the
validator logs "no structured pillars - skipped", never a silent pass.

R8 · Empty-RAG non-fatal + aspectRatio stamp. Empty retrieval -> empty validated set ->
grounded=false -> proceed (an ungrounded draft is valid, lower-quality - the gate's and
Phase 9's concern). Only schema-retry exhaustion or IChatClient down past retries fails
Strategist/CD. aspectRatio is stamped deterministically from PlatformConstraints by
target surface AFTER the CD call, overriding any model value (sharpest case of DL-030
inform-then-enforce); confirm the target surface is a readable RunState input.
Regenerate triggers remain schema + constraint only - no LLM-judge loop.
