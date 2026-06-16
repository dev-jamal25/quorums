# RunStatus state machine and the durable seam (DL-036, DL-037)

Two distinct concepts:

- **`RunStatus`** — the post lifecycle (persisted; what the dashboard shows).
- **`GraphPhase`** — the resume re-entry marker inside `RunState` (where `ResumeRun` re-enters the MAF graph).

## RunStatus members — APPEND, never renumber

Existing values are persisted; adding the Phase-6 states must not renumber them.

```csharp
public enum RunStatus
{
    Queued = 0,
    Running = 1,
    AwaitingApproval = 2,
    Publishing = 3,
    Done = 4,
    Failed = 5,
    Rejected = 6,
    Scheduled = 7,    // NEW (DL-037) — approved, waiting for its slot
    Cancelled = 8     // NEW (DL-037) — scheduled run pulled before it fired
}
```

If `RunStatus` is stored as an int via EF `HasConversion`, the numeric values above are load-bearing — never reorder. If stored as a string, the member names are load-bearing. Either way: append, do not renumber.

## Transitions

```
Queued ──▶ Running ──▶ AwaitingApproval
                            │
        approve (now) ──────┼──▶ Publishing ──▶ Done
                            │                      └─(failure)─▶ Failed
        approve (schedule) ─┼──▶ Scheduled ──(job fires)──▶ Publishing ──▶ Done
                            │         └──(cancel before fire)──▶ Cancelled
        reject ─────────────┼──▶ Rejected            (terminal)
                            │
        regenerate ─────────┴──▶ Running   ◀── NEW back-edge (DL-036); gate re-entrant
```

- **`AwaitingApproval → Running`** is the only back-edge; it exists for regenerate (DL-036). All other edges are forward.
- `Rejected` and `Cancelled` are terminal (no resume enqueued).
- The gate may be entered multiple times in one run, producing multiple `ApprovalAction` rows.
- A `Publishing` run that fails moves to `Failed` with a `ToolError` recorded (DL-022/039).

## GraphPhase (resume marker)

```
Strategy | Creative | Generation | Assembled | AwaitingApproval | Publishing | Done
```

- **Immediate or scheduled approve** re-enters at the same publish point. The `RunStatus` differs (`Publishing` vs a delayed job dispatched from `Scheduled`), but the graph re-entry is identical.
- **Regenerate** rewinds `GraphPhase`: `same-angle` re-enters at `Creative` (CD → Media); `reselect-angle` re-enters at the Supervisor selection step. The Supervisor writes the rewind (sole writer of `Phase`, DL-020).

## Durable seam

- The gate checkpoints `RunState` to `RunCheckpoint` (JSON) and ends `ExecuteRun`; state passes through Postgres, never through job payloads (DL-006).
- Approval enqueues `ResumeRun`: `Enqueue` for immediate, `Schedule(scheduledFor minus now)` for scheduled (DL-037). Hangfire persists both in the Postgres job store, so a delayed job survives worker restarts.
- `ResumeRun` reads `GraphPhase` to re-enter at the correct node.
- A retried or resumed segment is idempotent: MinIO keyed by `assetId`, publish keyed by `contentItemId` (DL-022). No duplicate asset, no double publish.
