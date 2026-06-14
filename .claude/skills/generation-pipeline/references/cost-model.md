# Cost model

> Frozen by DL-029, realizing the DL-023 pre-Media budget check. The one
> expensive, irreversible spend is the Gemini image, so the hard gate sits there;
> text costs are bounded and merely tracked. Prices change, so they live in
> config — never hardcoded, never recalled from memory.

## Contents
- [Two budget dimensions](#two-budget-dimensions)
- [Two enforcement points](#two-enforcement-points)
- [Provisioning](#provisioning)
- [Prices as config](#prices-as-config)
- [Degrade path](#degrade-path)
- [Estimate table](#estimate-table)

## Two budget dimensions

`RunState.Budget` carries two independent dimensions:

- **`TokenBudget`** — text agents, measured in **tokens** (in + out across the
  Strategist, Supervisor selection, Creative Director, Copywriting, and the
  query-transform calls).
- **`MediaBudget`** — images, measured as **count → dollars** (per-image Gemini
  price × image count).

Gate the Media node on the **media** dimension; apply the global ceiling on
**combined dollars**.

## Two enforcement points

Enforce at exactly two points; **track everywhere else** (every call updates
`Spent`, but only these two points can change control flow):

1. **Pre-Media gate** (deterministic, in the Supervisor control plane, before the
   Media node fires): is the next image affordable under `MediaBudget`? If yes →
   proceed. If no → **degrade** (see below). This is the realization of the
   DL-023 "check cost before the expensive node" rule.
2. **Global per-run dollar ceiling:** if combined spend grossly exceeds the
   ceiling at any check, **fail** the run with a structured error. This is the
   runaway backstop, distinct from the graceful media degrade.

Never overspend, never crash, never fail silently.

## Provisioning

- **Budget = expected-case × 1.5** safety margin. Size `TokenBudget` and
  `MediaBudget` from the estimate table's expected case, then multiply by 1.5.
- **Global hard ceiling = worst-case.** Worst case is **N = 3 candidates + 2
  retries per retryable agent** (the full fan-out with every retryable agent
  exhausting its retries). The ceiling sits here so a pathological run fails
  cleanly rather than draining budget.

## Prices as config

- Prices are **config-bound** (a typed options object), seeded with the **current
  live values pulled at build/config time**. Do **not** hardcode a price literal
  in agent code and do **not** recall a price from memory — both go stale and
  neither is defensible in review.
- **Langfuse captures actuals** per call; Phase 9 uses those actuals to refine the
  static estimates below. The estimates are planning numbers, not ground truth.

## Degrade path

Two outcomes, deliberately different:

- **Media-budget breach (pre-Media gate fails):** skip media → produce a
  **caption-only `ContentItemDraft`**, record the degrade reason in the trace,
  and **proceed to the human gate**. The run still delivers something a human can
  approve.
- **Global-ceiling breach:** **fail** the run with a structured error. No
  caption-only fallback — this is the runaway case, not the affordability case.

## Estimate table

Static planning estimates (in / out tokens) per call. `× retries` marks agents
whose cost multiplies under the bounded retry loop; the Strategist additionally
multiplies by the **N = 3** candidate fan-out.

| Call | Model | Est. in / out tokens | Notes |
|---|---|---|---|
| Content Strategist | Sonnet | ~3000 / ~600 | × 3 candidates, × retries |
| Supervisor selection | Sonnet | ~1000 / ~150 | single call |
| Creative Director | Sonnet | ~3000 / ~450 | × retries |
| Copywriting | Haiku | ~3000 / ~250 | × retries |
| Query-transform (×3) | Haiku | ~250 / ~120 each | three transforms per retrieval |
| Embed / rerank | local | $0 | self-hosted, no API cost |
| Media | Gemini | per-image price | the only hard-gated spend |

Use this table to seed the expected case for provisioning; replace with Langfuse
actuals as Phase 9 measures them.
