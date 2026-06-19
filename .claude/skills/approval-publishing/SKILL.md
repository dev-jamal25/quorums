---
name: approval-publishing
description: Implementation contract for the human-approval gate and publishing workflow of the Autonomous Digital Marketing Agency .NET backend (Api, Worker, Core, Infrastructure). Use when implementing or changing the approval gate, ApprovalsController, the approve / reject / regenerate / cancel actions, human edits to a draft caption or hashtags, scheduled publishing, the IMetaIntegration publish path, publish-failure handling and retries, the ApprovalAction audit record, the RunState checkpoint and resume seam around the gate, or the approval-review DTO and its typed frontend client. Encodes frozen decisions DL-035 through DL-042 plus DL-055 (dual-channel Instagram + Facebook Page publishing) plus inherited gate, idempotency, and cost invariants. Read the references files for the DTO, Meta integration, audit schema, and state-machine detail.
metadata:
  author: Autonomous Digital Marketing Agency (capstone)
  version: 1.1.0
  phase: "6 — Human Approval and Publishing Workflow"
  decisions: DL-035 through DL-042, plus DL-055 (dual-channel publishing)
---

# Approval and Publishing Subsystem

This skill is the **implementation contract** for Phase 6 — the human-approval gate and the publishing path. It turns an assembled `ContentItemDraft` into a published (mocked) Instagram post through a human-gated, durable, idempotent workflow. The rationale and the append-only decision record live in the architecture pillar (DL-035 through DL-041); **this skill is what you implement against — do not re-decide anything here.** Each rule carries its DL tag as a provenance breadcrumb, not as required reading; the rules below are self-sufficient.

The graph spine that precedes this subsystem: `Supervisor → Content Strategist (N=3) → Supervisor selection → Creative Director → Copywriting → Media Generation → [HUMAN GATE] → Publishing`. This skill owns the gate itself and everything from the gate onward.

## Critical invariants — do not violate

1. **Human gate before any publish or paid action.** Never auto-publish (DL-005/021).
2. **A human edit is an approval-decision overlay on `ApprovalAction`, NEVER a write to `RunState.Draft`.** `RunState.Draft` stays byte-identical to the AI output; the Supervisor remains its sole writer (DL-035/020). Phase-9 evaluation depends on the original draft being intact.
3. **Publish is idempotent, keyed by `(contentItemId, channel)`,** enforced by a pre-publish guard (DL-022/039/054). A retried Hangfire segment must never double-post; a content item publishes to each target channel as an independent crash-safe unit.
4. **The audit is durable, RLS-scoped, and NEVER behind the Langfuse config gate** (DL-040). Disabling tracing must not drop the audit.
5. **The Supervisor is the sole writer of `RunState.Phase`, `Draft`, `Budget`** (DL-020). A regenerate phase-rewind is a Supervisor write. Agents and handlers write only their declared slice.
6. **Failures surface as a structured `ToolError` on `RunState.Errors` — never an exception into the graph** (DL-022).
7. **Every brand-scoped read or write goes through RLS** (the `set_config('app.current_brand', …)` interceptor). A manual brand-id filter is never the isolation mechanism (DL-002/007).

## Gate decisions and endpoints (DL-036, DL-041)

Two endpoints, split by semantics:

- **`POST /runs/{id}/approval`** — the gate decision, decision-discriminated body:
  - `approve` — optional `edits` (caption/hashtags, DL-035) plus optional `scheduledFor` (DL-037).
  - `reject` — optional `reason`. Terminal: `AgentRun` becomes `Rejected`, no resume, phase unchanged.
  - `regenerate` — `reason?` plus `mode` (`same-angle` or `reselect-angle`, DL-036).
- **`POST /runs/{id}/cancel`** — a separate action on a `Scheduled` run (already past the gate), making it `Cancelled`.

Full request/response DTOs, and the **server-computed available-actions list** (the client never recomputes policy), are in `references/review-dto.md`.

## Human edits at the gate (DL-035)

- Editable surface: **caption text and hashtags only.** The image is not editable — a bad image is `reject` or `regenerate`. Agent-reasoning fields (`ContentStrategy`, `CreativeDirection`, `Grounding`) are immutable.
- The edit lands on `ApprovalAction` (`EditedCaption?`, `EditedHashtags?`). On `ResumeRun`, the Publishing node reads `RunState.Draft` and applies the overlay before publishing.
- Validate the edit at the API boundary with a FluentValidation validator running the **same `PlatformConstraints` check** (caption at most 2200 chars, hashtags at most 30) — fail-fast 400. Keep the publish-time re-check as the backstop (DL-030, defense-in-depth).

## Regenerate loop (DL-036)

- `same-angle` — re-enter at **Creative Director → Media** to regenerate the visual on the existing strategy; inject the human `reason` as a hint.
- `reselect-angle` — re-enter at **Supervisor selection** over the already-banked N=3 `ContentStrategy` candidates (DL-027) — no new Strategist call — then CD → Media on the newly selected angle.
- Hard-bounded by a **per-run regenerate count drawing from the DL-029 global cost ceiling.** When the ceiling is reached, regenerate is disabled (only approve / approve-with-edit / reject remain — reflected in the available-actions list).
- The Supervisor performs the phase rewind (sole writer of `Phase`). This adds the **`AwaitingApproval → Running` back-edge** — the gate is re-entrant; each visit appends an `ApprovalAction` row.

## Scheduled publishing (DL-037)

- Branch at the approval handler: immediate → `BackgroundJob.Enqueue(ResumeRun)`; scheduled → `BackgroundJob.Schedule(ResumeRun, scheduledFor minus now)` (Hangfire delayed job, persisted in the Postgres job store).
- New states: `Scheduled` (approved, waiting) and `Cancelled` (terminal). Cancel-before-fire calls Hangfire `Delete`, making the run `Cancelled`. Reschedule is cancel-then-reschedule, composed (no new transition).
- `scheduledFor` is UTC `timestamptz`, FluentValidation-required strictly in the future; brand-local display is a frontend concern.
- A delayed job survives worker restarts (it lives in Postgres) — the durable-resume guarantee holds across the schedule wait. Full state graph in `references/state-machine.md`.

## Publishing path — IMetaIntegration (DL-038, DL-039, DL-055)

- **Dual-channel (DL-055).** The integration is **channel-aware**: one `IMetaIntegration` publishes to **Instagram** (container → poll → `media_publish`) and **Facebook Page** (unpublished photo → feed post) behind the same three steps. `PublishRequest` carries `Channel` + `TargetId` (IG Business Account ID or Page ID) and a **Meta-reachable public** `MediaUrl`; the Publishing node loops the content item's target channels, each keyed `(contentItemId, channel)`. Send the **PNG as-is** (no JPEG conversion); enforce IG's aspect-ratio (4:5–1.91:1) and caption `#`→`%23` at the boundary. Detail in `references/meta-integration.md`.
- The real publish is **two-step plus a poll** exposed as SEPARATE `IMetaIntegration` operations — `CreateContainerAsync` (→ creationId) → `PollContainerAsync` → `PublishContainerAsync` (creationId → media id). It is **NOT naturally idempotent.**
- **Robust creation-id idempotency guard (keyed on `contentItemId`, source of truth = the persisted `PublishRecord`):** persist the container **`CreationId` immediately after create, BEFORE publish**, then key re-entry on persisted state — (a) no record → create, persist `CreationId`, poll, publish, finalize (`ExternalRef` + `Status=Published`); (b) record with `CreationId` and no `ExternalRef` → do NOT re-create — re-publish the same container (Meta dedups an already-published container) and finalize; (c) record finalized → skip, return the existing `ExternalRef`. A crash in the create→persist-`CreationId` window leaves only an orphan unpublished container (harmless), never a double post. (The weaker "check for an existing `ExternalRef`, skip if present" guard does NOT close the crash-after-publish-before-record window — a retry finds no record and re-publishes.)
- The step results are **typed**: `PublishContainerAsync` returns a `PublishResult` whose `status` is one of `Published`, `TransientFailure`, `TerminalFailure`, and create/poll return classified failures — classify from the contract, never by exception-sniffing.
  - **Transient** (network timeout, 5xx, rate-limit-with-retry-after, container still processing) → Hangfire automatic retry, **3 attempts, exponential backoff**, each guarded by the idempotency check.
  - **Terminal** (auth/token revoked, content policy-rejected, invalid media, rate-limit exhausted, account restricted) → **0 retries** → `ToolError` → `AgentRun` becomes `Failed`, reason surfaced to the reviewer.
- `MockMetaIntegration` must model the separate create/poll/publish steps for both channels with a fresh creation id per create (so a crashed create leaves a real orphan), make a re-publish of an already-published container **deduped to the same media id**, expose **injectable crash points in BOTH durability windows** (after-create-before-persist-`CreationId` and after-publish-before-record), and inject both failure classes deterministically for CI. A single-call mock is trivially idempotent and hides the real double-post bug. Full contract and failure taxonomy in `references/meta-integration.md`.

## Audit (DL-040)

- **Model A**, two records: `ApprovalAction` (human actions, append-only, one row per gate visit) plus a persisted `PublishResult` row (system publish outcome). The unified per-post timeline is a **read projection** merging the two by timestamp — not a third table.
- Audit rows are **durable, RLS-scoped, and independent of Langfuse.** The trace may fall back to `LocalTraceRecorder` or be absent; the audit always persists.
- `actor` is captured (for the team-collaboration drop-in) but is a fixed demo principal in MVP — no identity/auth system. Schema in `references/audit-schema.md`.

## Advanced-scope seams — shape for real, ship mocked (DL-038)

Mocks **honor the real contract shape**; they are not minimal passthroughs. This is design-for-later, not build-now:

- `IMetaIntegration` is the real two-step shape, so `LiveMetaIntegration` is a true drop-in.
- Publishing is **modality-aware** (image/video → feed/reel/story), reusing the per-surface `PlatformConstraints` (DL-030).
- `PublishResult` carries the **engagement-poll keys the Analytics agent will read** (Phase 7).
- The gate **generalizes** so a future human-gated paid action (boost via Ads, Phase 8) reuses the same gate plus `ApprovalAction`, not a bespoke second gate.

## Critical gotchas

1. **Two-step publish double-post.** A crash between `publish container` and recording the result, then a Hangfire retry, re-runs create+publish and posts twice. Persisting only the final `ExternalRef` does NOT prevent this (the crash leaves no record, so the retry re-publishes). The fix is to persist the `CreationId` BEFORE publish (committed in its own unit) and re-publish that same container on retry (Meta dedups). The mock MUST model both crash windows and the re-publish dedup — otherwise the idempotency test passes while the real path is broken (DL-039).
2. **Edit on `RunState.Draft` is wrong.** The edit is an approval-decision overlay on `ApprovalAction`; `RunState.Draft` must stay byte-identical to the AI output (DL-035/020).
3. **Audit behind the Langfuse gate is wrong.** If approval/publish records are written through the trace, turning Langfuse off silently drops the audit. Audit writes are durable Postgres rows, gated by nothing (DL-040).
4. **Renumbering the `RunStatus` enum is wrong.** `Scheduled` and `Cancelled` are **appended** members; never renumber existing values — they are persisted (see `references/state-machine.md`).
5. **Worker entrypoint and Hangfire schema race** (from `feat/brand-onboarding`): keep `ENTRYPOINT ["dotnet"]` plus `CMD`; the worker overrides via the compose `command`. If the assembly name is on `ENTRYPOINT`, the worker silently runs the API and jobs stay `Queued`. Both Api and Worker need `restart: unless-stopped` for the Hangfire `CREATE SCHEMA` race on a clean volume.

## Verification — run before declaring any task done

State the method, run it, read the output. Done = every applicable box green.

1. **Idempotency (per channel):** force a crash after publish but before the result-record (and separately after create but before the `CreationId` persist), retry, and confirm exactly one published media **per channel** — the retry recovers via the committed `CreationId` + re-publish dedup, keyed `(contentItemId, channel)`; a content item targeting both channels yields exactly one post on each; the mock models the separate steps plus both crash windows for both channels (not a one-call fake).
2. **Edit boundary:** an over-limit edit (caption over 2200 chars or over 30 hashtags) returns 400 at `/approval` and never reaches publish; `RunState.Draft` is unchanged after an approved-with-edit run.
3. **Regenerate ceiling:** regenerate is absent from available-actions once the regen ceiling is hit; only approve/reject remain.
4. **Cancel scope:** `/cancel` is valid only in `Scheduled`; cancel-before-fire deletes the Hangfire job, run becomes `Cancelled`, no publish.
5. **Failure classification:** a `TerminalFailure` fails the run with the reason surfaced and 0 retries; a `TransientFailure` retries at most 3 with backoff.
6. **Audit independence:** disable Langfuse and confirm the audit still persists; every audit read is RLS-scoped (run the `Category=Isolation` two-brand test).
7. **Durable resume:** kill the worker after the gate checkpoint (and while a job is `Scheduled`); approve or fire, and confirm `ResumeRun` completes with no data loss, no duplicate asset, no double publish.

## References

- `references/review-dto.md` — the approval-review GET DTO, the action request DTOs, and the server-computed available-actions list (DL-041).
- `references/meta-integration.md` — the **channel-aware** two-step `IMetaIntegration` contract, per-channel mechanics, the `PublishResult` failure taxonomy, the `(contentItemId, channel)` idempotency guard, retry config, and mock requirements (DL-038/039/054).
- `references/audit-schema.md` — `ApprovalAction` and persisted `PublishResult` schemas, RLS, the timeline projection, and the Langfuse-independence rule (DL-040).
- `references/state-machine.md` — the full `RunStatus` graph with the new states and back-edge, resume markers, and the durable seam (DL-036/037).
