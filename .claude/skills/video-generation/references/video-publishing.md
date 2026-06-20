# Video publishing — IG Reel + FB Page video (reference)

Governs the video path of `IMetaIntegration`. Encodes DL-058 Decision 4. Extends DL-055 (dual-channel, channel-aware idempotency) and DL-042 (two-step coordinator). Read `SKILL.md` first.

> Live dual-channel publishing for **images** is already real (`LiveMetaIntegration`, from the DL-055 work) — despite older docs that describe `LiveMetaIntegration` as a present-but-throwing seam. This slice adds the **video** branch to that real implementation and to the mock.

## Channel + idempotency (unchanged from DL-055)

`PublishChannel { Instagram, FacebookPage }`. One approved `ContentItem` fans out to BOTH channels **sequentially**, each finalized as a `(contentItemId, channel)` `PublishRecord`. **Idempotency key = `(contentItemId, channel)`** — a retry must not double-publish. `PostSurface` selects the surface (image → feed/photo; video → reel/FB-video).

## IG Reel flow

1. `POST /{ig-user-id}/media` with `media_type=REELS` and `video_url` = the public MinIO URL of the mp4, plus the caption with hashtags composed in via `CaptionComposer` (IG has no separate hashtags field — DL-055 audit #6). Returns a creation/container id.
2. Commit the `CreationId` **before** publishing (DL-042 two-step) so a crash mid-publish can be reasoned about.
3. **Poll** the container `status_code` to `FINISHED`. Reels processing is **longer** than image — surface a `ToolError` on timeout (never throw).
4. `POST /media_publish` with the creation id.

**Shipped:** IG reel reuses the IG image publish (`media_publish`) unchanged; only `CreateContainerAsync` differs (`media_type=REELS`+`video_url`).

**Poll — GENEROUS in-call loop (CORRECTED by the live smoke).** The first cut polled once per `ResumeRun` attempt, bounded by the Hangfire retry budget (~130s). Video transcoding (reel AND FB Page video) takes minutes and outran it: the post **succeeded on Meta** (it appears on the timeline) but our run was marked **Failed** because the poll gave up before `ready`/`FINISHED` — a false-negative. So `PollContainerAsync` now polls **in-call** for video surfaces, looping `?fields=status_code` (IG) / `?fields=status` (FB) until ready, bounded by `Meta:VideoPollTimeout` (default **8 min**, override `Meta__VideoPollTimeout`); only an explicit `ERROR`/`error` is terminal, in-progress/unknown stays transient so a slow-but-successful post isn't false-failed; timeout → transient (a Hangfire retry re-enters). **Image is unchanged** (FB photo immediate-ready, IG image one `status_code` GET).

## FB Page video flow

`POST /{page-id}/videos` with `file_url` = the public MinIO URL, plus a description. Poll processing, then publish. Requires the long-lived **Page** token (the same token works for IG; a User token causes FB `#200` "as the page itself").

**Shipped (CORRECTED by the live smoke):** a Page video is its OWN post type — it is **NOT** an unpublished container you attach to `/feed` via `media_fbid` like a photo. The first cut mirrored the FB *photo* two-step (`published=false` → poll → `/feed` attach) and **failed live: FB images publish, FB videos do not** (the `/feed` `media_fbid` attach is photos-only). The shipped flow:
> - **Create:** `POST /{page-id}/videos` with `file_url` + `description` (NO `published=false` — `/videos` posts the Page video itself, default published). The returned **video id is the post**, committed as the `CreationId`.
> - **Poll:** `GET /{video-id}?fields=status` until `status.video_status == "ready"` (`PollContainerAsync`; FB photo stays immediate-ready).
> - **Publish:** **no-op** — `PublishContainerAsync` returns the committed video id as the `ExternalRef` (the video was posted at create). Branches on the recovered `LivePublishContext.Surface` (video → no-op; photo → the `/feed` attach, unchanged).
>
> Idempotency note: because `/videos` posts at **create**, the residual double-post window is the **create→commit-`CreationId`** gap (documented deferred debt — "re-posting a FB video double-posts"); a crash in the **publish→finalize** window now re-enters the no-op cleanly (no double). FB **image** is unchanged (`/photos` `published=false` → `/feed` attach). Validated on a live Page; CI never makes this call (the mock is surface-agnostic for the coordinator/guard proofs).

## MinIO must serve the video for Meta's server-side fetch (new vs the image path)

Meta fetches the asset **server-side** from `video_url` / `file_url`. So:

- MinIO MUST return `Content-Type: video/mp4` for the object.
- MinIO MUST honor **HTTP range requests** (Meta's video ingest uses ranged GETs). The PNG/image path never needed range; video will hang or fail without it.
- `Storage:PublicBaseUrl` MUST include the **bucket** (MinIO serves at `/{bucket}/{key}`); a bare key → Graph `9004`.
- The bucket must be publicly readable for the fetch (`mc anonymous set download …`, as in the bring-up checklist).

**Confirmed (no code):** the content-type is the Slice-A `MinioStorage` fix (`.mp4 → video/mp4`, served via `WithContentType` on write). Meta fetches `Storage:PublicBaseUrl` (cloudflared → MinIO **direct**, NOT the app `/runs/{id}/media` proxy), and MinIO/S3 honors range requests natively while cloudflared passes the `Range`/`Accept-Ranges` headers through — so range works without code. (Only if the public URL is ever re-routed through the app proxy would the proxy need range support; it is not.)

## Mock fidelity (DL-055 lesson — do this from the start)

The mock `IMetaIntegration` MUST model REAL Meta:

- Re-publishing does NOT dedup. FB `/videos` called twice → a **second** post. IG re-`media_publish` of a published reel → **errors** (and the media id is not recoverable from the error).
- Do NOT encode the legacy "re-publish = same id, deduped" behavior — it masks the real failure mode and validates the wrong thing.
- The mock `externalRef` stays deterministic (`mock://meta/{DeterministicGuid(runId,"meta")}`, per the existing convention), but a **second** publish of the same `(contentItemId, channel)` must be prevented by the **idempotency guard**, not by a pretend dedup.

## Recovery posture (inherited; do NOT expand here)

Reuse the existing in-process recovery (the audit-#1 singleton context store). The **cross-process** publish→finalize residual is inherited **deferred debt** and now also covers video (re-`media_publish` of a published reel errors; re-posting a FB video double-posts). The full recover-by-lookup rework (IG: recover the published media id; FB: check-before-post) is a **separate tracked slice** — do NOT build it here.

## Config-gating

Video publish runs only under `Meta:Mode=live`; the mock is CI/default. Absent live config must not break the mock path or CI.

## Required proofs (Slice B) — shipped

1. **Per-channel video idempotency** (mock) — `VideoChannelPublishTests`: two clean publish attempts per channel for a reel item → ONE post per channel; the mock does NOT dedup video (surface-aware: image dedups, video does not — pinned by `MockMetaVideoFidelityTests`), and `PublishAttemptCount == 2` (publish ran once per channel, never four) proves the **guard** skipped the second, not a pretend dedup.
2. **Reel poll timeout → `ToolError`** (mock) — `VideoChannelPublishTests`: a container perpetually `processing` exhausts the bounded retry budget → run `Failed` with a `meta.publish_failed` `ToolError`, no hang, no graph exception.
3. **Image publish unchanged** — `DualChannelIdempotencyTests` + `PublishNodeTests` stay green (the FB image two-step + dedup are untouched; only new branches were added).
4. **Live check on owned accounts (MANUAL):** one approved video item → a real IG Reel + a real FB Page video (the bring-up checklist). Claude Code cannot publish to real Meta.
5. **Standard CLAUDE.md gates:** `dotnet build -warnaserror`, `dotnet format`, `dotnet test`; `Category=Isolation` (data access via `PublishRecord` — unchanged, stays green); `gitleaks` clean. **No schema change** (`Channel` + the unique index already exist from DL-055).
