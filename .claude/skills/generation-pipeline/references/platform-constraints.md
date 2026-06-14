# PlatformConstraints validators

> Frozen by DL-030. Hard platform rules are a **global, deterministic config** —
> the same for every tenant — and cover **structural/format limits only**. They
> are distinct from the per-brand *soft* `platform_guidance` RAG corpus, and
> distinct from content-policy compliance (which stays with the human gate). A
> hard limit must never depend on retrieval recall or on the LLM counting
> correctly.

## Contents
- [The constraint set](#the-constraint-set)
- [Inform + validate](#inform--validate)
- [Per-constraint remedy](#per-constraint-remedy)
- [Publish-time re-check](#publish-time-re-check)
- [Where it lives](#where-it-lives)

## The constraint set

Frozen — neither add nor remove constraints. Per-surface config; the demo runs on
`instagram_feed`; **adding a surface is config, not code**.

| surface | constraint | limit | owner agent |
|---|---|---|---|
| `instagram_feed` | hashtagCount | ≤ 30 | Copywriting |
| `instagram_feed` | captionLength | ≤ 2200 chars | Copywriting |
| `instagram_feed` | aspectRatio | 4:5 or 1:1 | Creative Director (brief) |
| `instagram_reel` | aspectRatio | 9:16 | Creative Director (brief) |
| `instagram_story` | aspectRatio | 9:16 | Creative Director (brief) |

## Inform + validate

Each constraint is applied **two ways**:

- **Inform** — the relevant constraint is injected into the owning agent's prompt
  (skeleton part 4) so the model aims to comply: Copywriting is told "≤ 30
  hashtags, ≤ 2200 chars"; the Creative Director is told the permitted aspect
  ratios for the surface.
- **Validate** — a **deterministic post-generation check** runs regardless of what
  the model produced. **Never trust the LLM to count.** A validation failure feeds
  the structured-output retry loop (see `output-schemas.md`) as a
  `PlatformConstraints` violation — one of the only two regenerate triggers.

## Per-constraint remedy

The remedy is **per-constraint and declared in config**, not improvised:

- **`hashtagCount` over → repair.** Drop the extra hashtags down to the limit and
  write a trace note. No regenerate — truncating a hashtag list is lossless enough
  that a repair is correct and cheaper than a re-roll.
- **`captionLength` over → regenerate.** Feed back "shorten to ≤ N chars" and
  retry. After the 2 bounded retries still over → **hard-truncate fallback** so the
  pipeline never emits an over-limit caption.
- **`aspectRatio` → pre-enforced, no post-hoc remedy.** The Creative Director sets
  `mediaPromptBrief.aspectRatio` to a surface-permitted value **before** Gemini is
  called. There is nothing to remedy after the fact because the constraint was
  satisfied at authoring time.

## Publish-time re-check

The publishing path calls the **same validator** before it publishes
(defense-in-depth). This catches edits a human made at the approval gate — e.g. a
reviewer who pastes in extra hashtags or lengthens the caption past the limit.
The validator is shared code, not a re-implementation, so the publish-time check
can never diverge from the generation-time check.

## Where it lives

The `PlatformConstraints` config and validator live in the **generation
pipeline** (this skill's domain) and are **invoked by both generation and
publishing**. Keep one definition; both call sites reference it. This is the
boundary that lets the publish-time re-check (DL-030) reuse the exact same rules
the generation loop enforced.
