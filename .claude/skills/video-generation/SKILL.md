---
name: video-content-pipeline
description: Implementation contract for video content in the Quorums marketing platform — Veo 3.1 video generation behind IMediaGenerationTool and dual-channel video publishing (Instagram Reel + Facebook Page video) behind IMetaIntegration. Use whenever implementing, modifying, reviewing, or debugging video generation, the Veo/Gemini media tool, image-to-video or text-to-video, the Media Generation MAF node for video, the video MediaBudget, or publishing video to Instagram Reels or a Facebook Page. Encodes DL-058. Does NOT cover image-only generation (see generation-pipeline) or carousel/multi-item posts (explicitly out of scope).
metadata:
  version: 1.0.0
  governing-dl: DL-058
  supersedes: DL-003 (video-deferred posture)
  extends: generation-pipeline, agent-orchestration-graph, marketing-agency-architecture
---

# Video Content Pipeline

Adds video to the existing image pipeline: Veo generation, then Instagram Reel + Facebook Page video publishing, through the same supervised graph, human gate, and idempotency that already prove out for images. This skill is the source-of-truth contract; the human rationale lives in DL-058 (System Architecture pillar doc), which Claude Code never reads.

## CRITICAL — invariants, read before changing anything

- **Video only.** Image generation and image publishing are unchanged and MUST keep working with all video config absent. Never regress the image path.
- **One `MediaAssetRef` per `ContentItem`. No carousel, no multi-item runs.** `RunState.Media` stays a single nullable ref. A run produces one content item. (Multiple assets in one post = carousel = out of scope; one run producing many curated items = multi-item model = out of scope. Both deferred in DL-058.)
- **The human gate owns every publish decision (DL-005).** Nothing auto-publishes. Video changes nothing here. The demo shows both an image post and a video post via two separate, independently gated runs.
- **Veo is asynchronous AND paid. The Media Generation node MUST be submit-or-resume idempotent.** On a retry within a run, resume the in-flight Veo operation; NEVER submit a second job. Re-submitting re-bills a paid clip and may never converge. (Gotcha #1 — see `references/veo-generation.md`.)
- **The op name does NOT live in `RunCheckpoint`.** `RunCheckpoint` is written only at the gate, after generation. The in-flight Veo operation name lives in a process-resident singleton store keyed by the deterministic `assetId`, mirroring the `LivePublishContextStore` singleton that fixed the publish-side audit #1.
- **Tool failures degrade, never throw (DL-022).** A Veo timeout/terminal error or a Meta error surfaces as a `ToolError` on `RunState.Errors`; the Supervisor degrades (caption-only) — never let an exception into the MAF graph.
- **Config-gated like Vault/Langfuse.** Absent/disabled Veo config MUST NOT crash startup or break image runs; register any Veo health check only when configured.
- **The mock MUST model REAL Meta.** Re-publish does NOT dedup (FB double-posts; IG re-`media_publish` errors). Do not encode the legacy "re-publish = same id, deduped" behavior — it validates the wrong thing (DL-055 lesson).
- **Same Gemini API key as Nano Banana (paid tier), via Vault `ISecretsProvider`.** Never inline a key, never log it.

## Where this plugs in

- **Generation:** extend the Media Generation MAF node (`Infrastructure/Orchestration/Maf/Nodes/`) and `IMediaGenerationTool`. Live = `LiveGeminiMediaTool` (add the Veo video path next to the Nano Banana image path); CI/offline = `DeterministicMediaGenerationTool` (add a deterministic video asset).
- **Publishing:** extend `IMetaIntegration` — the real `LiveMetaIntegration` (live image publishing already works per the DL-055 work, despite older docs calling it a throwing seam) and the mock — with the IG Reel + FB Page video flows. Channel and idempotency come from DL-055: `(contentItemId, channel)`.
- **Run flow (unchanged):** `Queued → Running` [Media Generation runs here] `→ AwaitingApproval` [gate, `RunCheckpoint` written here] `→` (approve) `→ Publishing` [video publish runs here] `→ Done`. Reject path unchanged.
- **Modality:** `image | video`, chosen per run. `PostSurface` selects feed/photo vs reel/FB-video.

## Implementation order — land each slice before the next (no parallel slices)

1. **Slice A — Veo generation ✅ SHIPPED**, proven end-to-end on the **mock** publish path. Detail: `references/veo-generation.md`. Shipped: `IVeoClient`/`LiveVeoClient`, `VeoVideoGenerator` (submit-or-resume async core), `VeoOperationStore` (singleton in-flight map), the `IMediaGenerationTool` request seam (`MediaGenerationRequest` carries the deterministic `assetId`), the deterministic mp4 stub. **Modality selection ✅ SHIPPED:** chosen on `POST /runs` (`{ modality, videoSource }`), persisted on the `AgentRun` row (`modality`/`video_source` columns, EF migration `Dl058RunModalitySelection`), read by `ExecuteRun` into `RunState` — DL-006: modality travels through Postgres, **never** the Hangfire payload, so a retry rebuilds it. A video run targets `instagram_reel` (9:16) and gets a larger flat media budget. (Dashboard toggle + gate `<video>` preview are a separate frontend pass.)
2. **Slice B — live IG Reel + FB Page video publish**. Detail: `references/video-publishing.md`.

## Generation summary (full detail: `references/veo-generation.md`)

- **Model** (`Veo:Model`), **9:16** (from `PlatformConstraints.instagram_reel`), duration **∈ {4, 6, 8}s** (`Veo:MaxDurationSec`). ⚠️ **Veo 3.x durations are DISCRETE — 4, 6, or 8 only** (5 is Veo-2-only → live `400 INVALID_ARGUMENT`); the old "4–5s" wording was wrong. At 9:16 use 720p (default; Veo 3 1080p/4k is 16:9-only and forces 8s). **Image-to-video is a per-model capability:** the `ImageSeed` first frame requires an image-capable model — the full **`veo-3.1-generate-preview`** is the proven one (every Gemini docs image-to-video example uses it). **`Fast` variants REJECT the first frame — live-confirmed on BOTH `veo-3.1-fast-generate-preview` AND `veo-3.0-fast-generate-001`** (`400 "inlineData isn't supported by this model"`); `Lite` is unverified for the first-frame `image` (it advertises *reference images*, a different input). So `VideoSource=ImageSeed` MUST target a full `*-generate-*` model (not `*-fast-*`/`*-lite-*`); pair `*-fast-*`/`*-lite-*` with `TextPrompt` only. The exact image-capable id depends on the key — verify in AI Studio.
- **Source modes** (`VideoSource`): `ImageSeed` (default) animates the Nano Banana image as Veo's first frame (needs an image-capable model — see above); `TextPrompt` does text-to-video from `MediaPromptBrief` (works on Fast/Lite). Same async core. `MediaPromptBrief` gains `Modality` + `DurationSec`.
- **Async core:** submit Veo op → record the operation name in the singleton in-flight store keyed by `assetId` (`= DeterministicGuid.From(runId,"asset")`) → poll bounded by `Veo:PollTimeout` (Polly) → download mp4 → `IStorageService` at `brands/{brandId}/assets/{assetId}.mp4`. **Submit-or-resume:** if the store already holds an op for this `assetId`, resume; never re-submit.
- **Degrade:** timeout/terminal error → `ToolError` → pre-Media degrade to caption-only (DL-023). **Budget:** `MediaBudget` gains a config video price (`Media:VideoPricePerSec` × duration); `ImageSeed` also charges the one image; the pre-Media gate degrades if unaffordable; submit-or-resume means a retry does not re-charge.

## Publishing summary (full detail: `references/video-publishing.md`)

- **IG Reel:** `POST /{ig-user-id}/media` `media_type=REELS` + `video_url` → poll container `status_code` to `FINISHED` (longer than image) → `POST /media_publish`. **FB Page video:** `POST /{page-id}/videos` + `file_url` → poll processing if needed → published.
- **Dual-channel sequential, idempotent on `(contentItemId, channel)`** (DL-055). `PublishChannel` unchanged.
- **Meta fetches the asset from the public MinIO URL** → MinIO MUST serve the mp4 with `Content-Type: video/mp4` AND honor HTTP range requests (the PNG path did not need range). `Storage:PublicBaseUrl` must include the bucket.
- **Recovery:** reuse the existing in-process recovery (audit-#1 singleton). The cross-process publish→finalize residual is inherited deferred debt (now also covering video). Do NOT build recover-by-lookup here.

## Configuration (all config-bound; never hardcode; absence must not crash)

`Veo:Mode` (`mock`|`live`, default `mock` — live wires `LiveVeoClient` + `VeoVideoGenerator`), `Veo:Model`, `Veo:MaxDurationSec` (**must be 4, 6, or 8** — Veo 3.x discrete durations; the shipped default of `5` is INVALID and 400s a live run, fix pending), `Veo:PollTimeout` (e.g. `"00:10:00"` — Veo is slow; 3 min often times out), `Veo:PollInterval`, `Media:VideoPricePerSec`. ⚠️ **Default-coherence (DL-058 cleanup):** the shipped `Veo:Model` default is a Fast *preview* (not image-capable) while `VideoSource` defaults to `ImageSeed` — incompatible; for an image-seed run set `Veo:Model` to an image-capable model (e.g. `veo-3.1-generate-preview`, or Veo 3 GA `veo-3.0-*-generate-001` if your key supports image input). Gemini key via Vault (same key as Nano Banana). `Meta:Mode` (`mock`|`live`) unchanged. All `Veo`/`Media` keys are optional-with-defaults — absence never crashes startup or breaks image runs; the Veo health check registers only when `Veo:Mode=live`.

## Required tests (adversarial proofs — Done = green, per the CLAUDE.md checklist)

- **Slice A:** (1) an in-process retry of the Media node **resumes the in-flight Veo op and submits ZERO new jobs** (fake Veo client + submit counter == 1). (2) Veo poll **timeout → `ToolError` → caption-only**. (3) video **`MediaBudget` breach → caption-only**. Plus end-to-end on `Meta:Mode=mock` with a real mp4 at `brands/{brandId}/assets/{assetId}.mp4`.
- **Slice B:** per-channel **video idempotency** — two publishes of the same `(contentItemId, channel)` yield ONE post; the mock models real Meta (no dedup), so the guard is what prevents the double. Live check on owned IG/FB.
- **Standard gates:** `dotnet build Backend.sln -warnaserror`, `dotnet format --verify-no-changes`, `dotnet test`; `dotnet test --filter Category=Isolation` if data access is touched; `gitleaks` clean; an EF migration with RLS for any new brand-scoped table. (The in-flight op store is in-memory → no migration unless you choose the durable variant in `references/veo-generation.md`.)

## References

- `references/veo-generation.md` — the Veo async core, source modes, budget, CI stub, the singleton in-flight store, and the gotchas in depth.
- `references/video-publishing.md` — IG Reel + FB Page video Graph flows, idempotency, MinIO range serving, mock fidelity, recovery posture.

## Common issues

- **Re-billing on retry** → the op name is in the wrong place. It must be in the singleton in-flight store keyed by `assetId` and checked at node entry. `RunCheckpoint` (gate-only, post-generation) is the WRONG place.
- **Video publishes but Meta `9004` / ingest hangs** → MinIO not serving `video/mp4`, not honoring range requests, or `Storage:PublicBaseUrl` missing the bucket.
- **FB `#200` "as the page itself"** → use the long-lived Page token (works for IG too).
- **Startup breaks with no Veo key** → not config-gated; mirror the Vault health-check registration pattern.
