# DL-058 — Video generation + dual-channel video publishing on the demo path

> Append-only. Freeze on creation; supersede with a new entry rather than editing in place.
> Lives in the **System Architecture** pillar doc (human record; Claude Code never reads it).
> **Supersedes** (1) the "video deferred to advanced scope / off the MVP demo critical path" posture of
> **DL-003** (video now runs on the demo critical path; add a supersede pointer at DL-003); and (2) the
> **video portion of DL-023's "media-generation failure → fail-item" policy** — video-generation failure
> degrades to caption-only instead (image failure stays fail-item; see Decision 2).
> **Extends** DL-023/DL-029 (the video `MediaBudget` ceiling is now a real, enforced number),
> DL-042 (two-step publish coordinator gains a reel/video variant), DL-055 (dual-channel publish —
> modality is now `image|video`), DL-022 (idempotency now also covers the Veo long-running operation
> and the video publish), DL-028 (`MediaPromptBrief` gains modality/duration), DL-030
> (`instagram_reel` 9:16 already present; reused for video), DL-005 (the human gate stands between
> generation and publishing for every content item).
> **Does NOT resolve** the DL-055 recover-by-lookup deferred debt — see Trade-offs.

---

### Decision

**1. Video onto the demo critical path; modality is a per-run choice; the demo exercises both.**
A run's media modality is `image | video`, selected per run. **Image stays the default and the proven live
fallback.** No carousel; **exactly one `MediaAssetRef` per `ContentItem`** (`RunState.Media` remains a single
nullable ref — unchanged). The dual-channel fan-out is unchanged: one approved `ContentItem` publishes to
**IG + FB as two sequential `PublishRecord`s** keyed on `(contentItemId, channel)`. "Two posts back to back" =
the two channels, not two content items.
- **Demo behavior:** the demo **creates both an image post and a video post via two separate runs** (one
  image, one video). **Each run is independently human-gated; nothing publishes without gate approval** —
  generation is automatic, the publish decision is the human's (DL-005). This satisfies "both content created,
  human decides what publishes" with no spine change.
- **Deferred (and explicitly *not* the carousel):** producing both modalities **within a single run** as a
  curated candidate set — the human gate selecting from a *set* of content items — is a multi-item `RunState`
  model change (single-item run → set-of-items run, touching `Media`/`Draft`/`Approval`/`Publish`). Out of
  scope here. Distinct from the rejected carousel (which is multi-asset within one item).

**2. Generation — Veo 3.x, {4,6,8}s, 9:16, two source modes.**

> **CORRECTION (live-tested, supersedes the Fast/Lite + "4–5s" wording below):** Veo 3.x `durationSeconds`
> is **discrete — 4, 6, or 8** (not "4–5s"; 5 is Veo-2-only and live-400s `INVALID_ARGUMENT`). And
> **image-to-video (`ImageSeed` first frame) is a per-model capability**: the full `veo-3.1-generate-preview`
> accepts the first frame; the **Fast/Lite *preview* variants reject it** (`400 "inlineData isn't supported
> by this model"`). So `ImageSeed` needs an image-capable model; Fast/Lite previews are text-to-video only.
> The original "Fast/Lite for cost" intent still holds for `TextPrompt`. Full detail: `references/veo-generation.md`.

- **Model:** config-bound (`Veo:Model`), on the Gemini Developer API
  (`generativelanguage.googleapis.com/v1beta`, `x-goog-api-key`) — the same API family as Nano Banana, not
  Vertex. **Image-capable** model for `ImageSeed` (e.g. `veo-3.1-generate-preview`); Fast/Lite for `TextPrompt`.
- **Clip:** **{4, 6, 8}s** (`Veo:MaxDurationSec` — must be one of these), **9:16 portrait** (set from
  `PlatformConstraints.instagram_reel`, DL-030; 9:16 ⇒ 720p, since Veo 3 1080p/4k is 16:9-only).
- **Source modes** (request param `VideoSource`): **`ImageSeed` (default)** — animate the Nano Banana image
  as the Veo first frame (brand-consistent; reuses the proven image gen); **`TextPrompt`** — text-to-video
  from the `MediaPromptBrief`. Both share one async core; the modes are thin input variants.
- **Output:** mp4 → MinIO; `MediaAssetRef` carries `Modality=Video`, `DurationSec`, mime, and the public-URL
  seam Meta fetches at publish.
- **Async-operation contract (core durability decision):** Veo is a **long-running operation**
  (submit → poll the returned operation name → download the result URI). The **operation name is held in a
  process-resident singleton in-flight store keyed by the deterministic `assetId`** (mirroring
  `LivePublishContextStore` from audit #1 — NOT `RunCheckpoint`, which is written only at the gate, after
  generation completes), and the generation node is **submit-or-resume idempotent on `assetId`** via **two layers**: (a) **op-level** —
  the in-flight store resumes a within-node/Polly retry on the same operation; (b) **asset-level** — the node's
  first action checks whether the committed `brands/{brandId}/assets/{assetId}.mp4` already exists and reuses
  it if so, so a cross-`ExecuteRun` retry after a successful generation does not re-bill. A within-run retry
  therefore **NEVER submits a new Veo job** (gotcha #1 — re-submission re-bills a paid job and may never
  converge). Polling is **bounded by a config timeout**; on timeout it returns a `ToolError` and degrades per
  the degradation policy below — never an exception into the MAF graph (DL-022, gotcha #2).
- **Degradation policy (supersedes the video portion of DL-023).** Video-generation failure — **timeout OR a
  terminal Veo error**, after the bounded poll — degrades to a **caption-only** `ContentItemDraft` that reaches
  the human gate. **Image-generation failure stays fail-item** (DL-023 unchanged for image). Rationale: the
  video model is a slow, flaky external dependency, so graceful degradation to the human gate (who can
  regenerate or proceed caption-only) beats failing the run. Budget-breach → caption-only and global-ceiling →
  run-fails are unchanged (DL-023/DL-029).

**3. Budget — real video ceiling, retry must not re-bill.**
`MediaBudget` gains a **video price** (config: per-second × duration; DL-029 — prices pulled at build time,
never hardcoded/recalled). Veo is ~1–2 orders of magnitude above the $0.039 image; with `ImageSeed` the run
also pays the one Nano Banana image. The **pre-Media gate degrades to caption-only** if video is unaffordable
(DL-023/029 mechanism unchanged). The submit-or-resume rule guarantees a retry does **not** re-bill. **Video runs
carry a higher flat media-budget ceiling than image (≈$5 vs the image's $1)** so a real ~$2 Fast clip isn't
forced to caption-only by the ceiling itself; the per-second price stays config-bound (`Media:VideoPricePerSec`)
and the gate still enforces affordability. Both flat ceilings are hardcoded today (matching the pre-existing
image budget) — config-binding them is a later cleanup.

**4. Publishing — IG Reel + FB Page video, dual-channel, sequential.**
- **IG Reel:** `POST /{ig-user-id}/media` with `media_type=REELS` + `video_url` → poll container `status_code`
  to `FINISHED` (**longer window than image**) → `POST /media_publish`. Carries the DL-042 two-step shape
  (CreationId committed before publish) and the DL-055 `(contentItemId, channel)` idempotency.
- **FB Page video:** `POST /{page-id}/videos` with `file_url` (+ description) → poll processing if required →
  published.
- The PNG→image path is unchanged; the mp4→video path is a **new modality branch** behind `PostSurface` /
  modality on `IMetaIntegration`. Same long-lived **Page** token, same Transit-encrypted
  `BrandMetaConnection`, same `Meta:Mode=live` gating.
- **Mock models real Meta from the start** (carry the DL-055 lesson in immediately): re-publish does **not**
  dedup (FB double-posts, IG re-`media_publish` errors) — the mock must not validate the wrong behavior.

**5. Required adversarial proofs (cover both gotchas):**
- *(Slice A)* A retry of the generation node **resumes the existing Veo operation and submits zero new Veo
  jobs** (assert via durable op name + a fake-Veo submit counter).
- *(Slice A)* Poll **timeout → `ToolError` → caption-only**; **video budget breach → caption-only** (no hang,
  no crash).
- *(Slice B)* **Per-channel video idempotency** — re-running publish for the same `(contentItemId, channel)`
  does not double-publish; the mock models real Meta (no re-publish dedup).

---

### Rationale

Video is the project plan's named gap (Core Feature "Gemini-powered video generation"; reels; Phase 2). The
architecture anticipated it (DL-003 "same loop, more expensive tool, same interface"; `IMediaGenerationTool`
modality param; DL-023/029 video ceiling), so this flips a designed seam rather than reshaping the graph. The
one real divergence from the "same interface" framing is that Veo is **asynchronous and paid**, which is why
the durability of the operation (checkpoint + submit-or-resume) is a first-class decision and not an
implementation detail. `ImageSeed` as the default reuses the proven Nano Banana path and yields a stronger,
brand-consistent demo. Running both modalities as two gated runs keeps the demo's "generate-many,
human-curates" story without a multi-item model change under deadline.

### Defensibility one-liner

"Video is the same supervised loop with a more expensive, asynchronous tool — Veo behind the existing media
interface, its long-running operation tracked so a retry resumes (or reuses the already-committed asset) rather
than re-bills, published as an IG
Reel / FB video through the same dual-channel coordinator and idempotency that proved out for images — and the
human gate still owns every publish decision."

### Trade-offs

- Veo per-second cost — mitigated by the short {4,6,8}s clip (4s cheapest) + retry-no-rebill; Fast/Lite cut cost for `TextPrompt`, but `ImageSeed` needs an image-capable model (see the §2 correction).
- **Generation residual (deferred):** a hard crash in the microsecond between the Veo submit and the in-flight
  `Set`, on a cross-process restart, can orphan **one** paid job (the asset-level check bounds it to one). Veo
  exposes no client idempotency id, so the full close is a durable `assetId → operation` table — deferred.
- Longer poll windows on both generation and the IG reel container — bounded by config timeouts with a defined
  degrade.
- Live video publish is exercised late, so it is **config-gated with the proven live image path as the
  fallback** for the demo.
- The "both content created" demo is achieved by **two gated runs**, not a single-run curated set; the
  single-run multi-item model is deferred (see Decision 1).
- **Recovery debt inherited, not resolved:** the video publish path reuses the existing in-process recovery
  (the audit-#1 singleton context store) and **inherits the same documented residual** (cross-process restart
  in the publish→finalize window). The DL-055 recover-by-lookup rework remains the separately-tracked deferred
  debt and **now also covers the video flows** (re-`media_publish` of a published reel errors; re-posting a FB
  video double-posts). It is explicitly **not** in scope here.

### Success signal

End-to-end: choose video → Veo generates a {4,6,8}s 9:16 clip (image-seeded or text) → mp4 in MinIO → gate →
approve → **IG Reel + FB Page video both live**. A retry mid-generation resumes the same operation (zero new
jobs). A forced budget breach degrades to caption-only. The demo shows both an approved image post and an
approved video post, each gated. Runs remain replayable/scoreable in Phase 9 like image runs.

### Impacted phases

5 (generation), 6 (publishing), 9 (eval).

### Skill-spec notes

- **`IMediaGenerationTool`:** add a Veo implementation; modality `image|video` param; `MediaAssetRef` gains
  `Modality`, `DurationSec`, mime. **Veo operation name held in a singleton in-flight store keyed by the deterministic `assetId`** (mirror
  `LivePublishContextStore`; NOT `RunCheckpoint`, which is gate-only); generation node = **submit-or-resume**
  (idempotent on `assetId`); poll bounded by `Veo:PollTimeout`.
- **`RunState.Media`** stays a single `MediaAssetRef?` (no carousel, no multi-item — confirmed).
- **`MediaPromptBrief`** (DL-028): add `Modality` + `DurationSec`; `aspectRatio` = 9:16 for video from
  `PlatformConstraints.instagram_reel` (DL-030). `VideoSource = ImageSeed | TextPrompt` on the generation
  request; default `ImageSeed` (Nano Banana frame as Veo first frame / reference).
- **`MediaBudget`** (DL-029): add config-bound video price (per-second × duration); pre-Media gate covers
  video; `ImageSeed` adds the one image cost.
- **`IMetaIntegration`:** video branch per channel — IG `media_type=REELS` + `video_url` (container → poll →
  publish); FB `/{page-id}/videos` + `file_url`. `PublishChannel` unchanged; `PostSurface` selects reel/video.
  **Mock models real Meta** (no re-publish dedup).
- **Bring-up / serving:** MinIO must serve mp4 with `Content-Type: video/mp4` and honor **HTTP range
  requests** (Meta's video ingest needs it; the PNG path did not). `Storage:PublicBaseUrl` must still include
  the bucket.
- **Config (all config-bound, no hardcode):** `Veo:Model` (image-capable for `ImageSeed`, e.g.
  `veo-3.1-generate-preview`; Fast/Lite for `TextPrompt`), `Veo:MaxDurationSec` (**4, 6, or 8** — not 5),
  `Veo:PollTimeout` (Veo is slow — minutes), `Media:VideoPricePerSec`. Disabled/absent Veo key must never
  crash startup or image runs.
