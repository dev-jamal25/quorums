# Output schemas + enforcement

> Frozen by DL-028 (with `pillar` validation from DL-026). The **C# record is the
> single source of truth**; the JSON schema handed to the forced tool is
> **derived** from the record — never hand-maintained alongside it, or the two
> drift. Every agent output is a typed, schema-validated record; **no agent ever
> hands off free text**.

## Contents
- [Canonical records](#canonical-records)
- [Enforcement: forced-tool + record-first](#enforcement-forced-tool--record-first)
- [Validation + retry loop](#validation--retry-loop)
- [pillar validation (DL-026)](#pillar-validation-dl-026)

## Canonical records

These are the canonical definitions. Express the JSON schema for each forced tool
by deriving it from the record (e.g. a schema generator over the record type) so
there is exactly one place a field is defined.

```csharp
// Strategist output envelope — N = 3 candidates (DL-027)
record StrategyCandidates(IReadOnlyList<ContentStrategy> Candidates); // length == 3

record ContentStrategy(
    string          Pillar,          // validated ∈ brand playbook pillars at receipt (DL-026)
    string          Angle,
    Objective       Objective,       // fixed enum
    string          Audience,        // persona ref + short elaboration
    string          AngleRationale,  // one line — drives Supervisor selection + trace
    string?         CalendarSlot,    // optional — designed-for / advanced
    Grounding       Grounding
);

enum Objective { Awareness, Engagement, Conversion, Traffic, Retention }

record SelectionDecision(
    int    ChosenIndex,              // in [0, 2]
    string Rationale
);

record CreativeDirection(
    string                       VisualConcept,
    IReadOnlyList<string>        StyleTokens,
    IReadOnlyList<ColorToken>    ColorTokens,
    MediaPromptBrief             MediaPromptBrief,
    Grounding                    Grounding
);

record ColorToken(string Name, string Hex);

// Structured — the Media node renders this to the Gemini prompt; NOT free text.
record MediaPromptBrief(
    string  Subject,
    string  Style,
    string  Composition,
    string  Palette,
    string  Mood,
    string? Negative,
    string  AspectRatio              // set from PlatformConstraints for the surface (DL-030)
);

record Caption(
    string                Hook,
    string                Body,
    IReadOnlyList<string> Hashtags,  // maxItems from PlatformConstraints (DL-030)
    Grounding             Grounding
);

// On EVERY agent output.
record Grounding(
    bool                  Grounded,
    IReadOnlyList<string> ChunkIdsUsed,
    Confidence            Confidence
);

enum Confidence { Low, Medium, High }
```

## Enforcement: forced-tool + record-first

- **Forced-tool / JSON-schema output for every agent.** The schema is the tool's
  input schema; the model is required to emit a `tool_use` block, and you
  deserialize `tool_use.input` into the record. There is no free-text parsing
  path — if it does not deserialize, it failed.
- **Record-first derivation.** The record is canonical; generate the tool's JSON
  schema from it. Do not keep a separate hand-written JSON schema — a hand-kept
  dual is the classic drift bug (a field renamed in the record but not the schema
  passes the build and fails silently at runtime).
- **`MediaPromptBrief` is structured on purpose.** It guards the one paid external
  call (Gemini). Keeping it a typed object — not a free-text string — is what lets
  the aspect-ratio constraint be set and checked deterministically before spend.

## Validation + retry loop

Validate **on receipt**, then a **bounded retry of 2**, then `ToolError`:

```
receive tool_use.input
  → deserialize into record         (deserialization failure = schema violation)
  → validate record:
        • schema/shape valid?
        • pillar ∈ brand pillars?    (DL-026 — schema-level)
        • PlatformConstraints pass?  (DL-030)
  → if all pass: accept, write the RunState slice
  → else: feed the SPECIFIC validation error back into the retry prompt, retry
  → after 2 failed retries: return ToolError
        → the Supervisor's deterministic control plane degrades or fails
          per DL-022/023 (Strategist & Creative Director are fatal nodes;
          others degrade per node policy)
```

The **only two** triggers that cause a regenerate are a **schema violation** and
a **`PlatformConstraints` violation** (DL-027). Nothing else loops. Always feed
the concrete error ("hashtags=34, limit=30" / "pillar 'Sustainability' not in
[Origin, Craft, Ritual]") back into the retry so the model can correct, rather
than re-rolling blind.

## pillar validation (DL-026)

`pillar` is a free string in the schema but is **validated against the brand's
playbook pillars at receipt**. The brand's pillar list is read at validation time
(it is brand-scoped data under RLS, not a constant). A `pillar` outside that list
is treated as a **schema-level violation** and triggers a regenerate through the
same loop above — it is not silently accepted and not a separate code path.
