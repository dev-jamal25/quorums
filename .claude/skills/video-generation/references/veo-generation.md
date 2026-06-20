# Veo video generation (reference)

Governs the video path of `IMediaGenerationTool`. Encodes DL-058 Decisions 2–3. Read `SKILL.md` first for the invariants.

## The interface (DL-001 — Gemini is a tool, never an orchestrator)

`IMediaGenerationTool` is the seam. **Shipped (Slice A):** the seam takes a `MediaGenerationRequest(MediaPromptBrief Brief, Guid AssetId, VideoSource Source)` and returns `MediaResult(byte[] Bytes, string MimeType, int? DurationSec)` — the `AssetId` is the deterministic key the video path keys its in-flight op on; `Brief.Modality` (`image`/`video`) selects the path. Two implementations gain a video path:

- **`LiveGeminiMediaTool`** — the Veo video path sits alongside the Nano Banana image path. For `ImageSeed` it reuses its own image call (`GenerateImageBytesAsync`) for the first frame, then delegates to **`VeoVideoGenerator`** (the submit-or-resume async core) over **`IVeoClient`** (live = `LiveVeoClient`, a typed `HttpClient`). Same Gemini API key (Vault `ISecretsProvider`, the key already provisioned for Nano Banana), paid tier. The Veo client/generator are registered ONLY when `Veo:Mode=live`; otherwise the optional `VeoVideoGenerator` is null and a video request throws → the node degrades.
- **`DeterministicMediaGenerationTool`** — the CI/offline stub. Returns a fixed small structurally-valid mp4 (an ISO-BMFF `ftyp`+`free` container) at `video/mp4` with the brief's `DurationSec`, so video runs are replayable with no spend and no network. Eval runs with blanked API keys (DL-053) still produce a video `MediaAssetRef`.

## Source modes

`VideoSource` on the generation request:

- **`ImageSeed` (default):** generate the Nano Banana image (existing path) → pass it to Veo as the first frame / reference image → Veo animates it. Brand-consistent. The run pays for the image AND the video.
- **`TextPrompt`:** text-to-video directly from `MediaPromptBrief`. Veo only.

Both render the `MediaPromptBrief` (DL-028: subject/style/composition/palette/mood + aspectRatio). For video, `aspectRatio` is **9:16** from `PlatformConstraints.instagram_reel` (DL-030); `MediaPromptBrief` gains `Modality` + `DurationSec`.

## The Gemini Veo API (confirmed wire shape — Slice A)

Veo 3.1 is on the Gemini Developer API (`generativelanguage` base URL, `x-goog-api-key`) — the same key family as Nano Banana. It is a **long-running operation**. Confirmed against `ai.google.dev/gemini-api/docs/video` and implemented in `LiveVeoClient`:

1. **Submit:** `POST {ApiVersion}/models/{model}:predictLongRunning` with body
   `{ "instances": [{ "prompt": "…", "image": { "inlineData": { "mimeType", "data" } } }], "parameters": { "aspectRatio": "9:16", "durationSeconds": 6 } }`.
   The `image` part is the image-seed first frame (omitted for text-to-video). Response returns `{ "name": "models/…/operations/…" }` — the operation name, **not** the video.
2. **Poll:** `GET {ApiVersion}/{operationName}` until `"done": true`. The mp4 URI is at
   `response.generateVideoResponse.generatedSamples[0].video.uri`; a terminal failure is in `error`.
3. **Download:** `GET {uri}` with the same `x-goog-api-key` header (follows redirects).

> ⚠️ **Two live-tested gotchas the docs bury (learned on the live demo):**
> - **`durationSeconds` is a JSON NUMBER and DISCRETE: `4 | 6 | 8` for Veo 3/3.1** (Veo 2 also allows `5`). TWO live-confirmed traps: (a) it must serialize as a **number** (`6`), NOT a string (`"6"`) → else `400 "The value type for durationSeconds needs to be a number"` (the docs' curl examples quote it, but that's shell interpolation); (b) the value MUST be 4/6/8 — the shipped `Veo:MaxDurationSec` default `5` 400s every Veo 3.x run. At 9:16 use 720p (the default); Veo 3 1080p/4k is 16:9-only and forces `8`.
> - **`image.inlineData` (the first frame) is a per-MODEL capability, not a field choice.** The shape above is correct; the full **`veo-3.1-generate-preview`** accepts it (every docs image-to-video example uses it), but **`*-fast-*` variants REJECT it — live-confirmed on BOTH `veo-3.1-fast-generate-preview` AND `veo-3.0-fast-generate-001`**: `400 "inlineData isn't supported by this model"`. `*-lite-*` is unverified for the first-frame `image` (it lists *reference images*, a separate input). Image-seed therefore needs a full `*-generate-*` model; pair Fast/Lite with `TextPrompt` only. **It is also per-KEY:** some API keys have NO image-to-video on ANY Veo model — live-confirmed on a key where the first-frame `400` hit Fast-preview, Fast-GA, AND full-GA (`veo-3.0-generate-001`). On such a key `ImageSeed` is impossible; the run MUST use `TextPrompt` (no first frame), which works on any Veo model incl. Fast.

Model ids: `veo-3.1-generate-preview` (full — **image-capable**, use for `ImageSeed`), `veo-3.1-fast-generate-preview` / `veo-3.1-lite-generate-preview` (**preview, reject the first frame** — `TextPrompt` only), plus Veo 3 GA `veo-3.0-generate-001` / `veo-3.0-fast-generate-001` (paid billing; "most Veo 3.x support image input" — verify the Fast GA accepts the first frame on your key before relying on it). Fast/Lite are cheaper/faster but only for text-to-video; full Veo 3.1 is premium per-second but the proven image-to-video path.

## The async core (gotchas #1 and #2 live here)

The Media Generation node runs during the **Running** phase, BEFORE the gate checkpoint. **There is no `RunCheckpoint` yet during generation** — it is written at the gate. So the in-flight Veo operation name MUST live in a **process-resident singleton store keyed by the deterministic `assetId`** — shipped as **`VeoOperationStore`** (`ConcurrentDictionary<Guid,string>`, `AddSingleton` always), a direct mirror of the `LivePublishContextStore` singleton that fixed the publish-side audit #1.

Submit-or-resume — **shipped in `VeoVideoGenerator`**, idempotent on `assetId = DeterministicGuid.From(runId,"asset")`:

- If `VeoOperationStore.TryGet(assetId)` holds an op → **resume polling it. Do NOT submit again.**
- Else → `SubmitAsync` → `Set(assetId, op)` **before any polling** → poll.

This makes an in-process Hangfire/Polly retry of the node resume the existing paid op and submit zero new jobs (proven by `VeoVideoGeneratorTests` with a fake client + submit counter == 1).

**Asset-level idempotency (node entry — the OUTER layer).** Op-level resume alone does NOT cover a **whole-`ExecuteRun` retry after a prior successful generation**: the op was evicted at commit, so the re-run would miss the store and submit a fresh paid job. So the Media node's FIRST action (for video, before the budget gate, the seed-image sub-call, AND the Veo submit) is an existence check on the deterministic `brands/{brandId}/assets/{assetId}.mp4` key (`IStorageService.ExistsAsync`). If it exists, the node reuses it — builds the `MediaAssetRef` from the committed object, records a `minio.exists`/`ok` span, and returns (no budget re-check; the asset is already paid). MinIO PutObject is atomic, so an existing object is complete. Proven by `VideoGenerationTests` (asset present → `RecordingMediaGenerationTool.Calls == 0`, i.e. zero Veo submits AND zero seed-image generations). Two layers together: **op-level** (within-node/Polly retry resumes the op) + **asset-level** (cross-`ExecuteRun` retry reuses the committed asset).

**Polling:** a deadline loop bounded by `Veo:PollTimeout` (Polly handles transient/429 on each HTTP call; the loop owns the overall deadline). On timeout **or terminal Veo error** (4xx / content-policy / 5xx-after-retries) `VeoVideoGenerator` throws `VeoGenerationException`; every throw path in `VeoVideoGenerator`/`LiveVeoClient` (submit, poll, JSON parse, download) propagates to the Media node's single `catch` and — because the run is **video** — degrades to **caption-only** (a non-fatal `ToolError` on `RunState.Errors` + a `degraded` trace span), never throwing into the graph (DL-022/023). Both failure modes have a proof (`VideoGenerationTests`: timeout AND terminal). **Asymmetry (shipped, supersedes DL-023's video portion):** an **image** generation failure stays **fatal** (unchanged — no regression); only **video** timeout/terminal degrades. (`OperationCanceledException` is deliberately NOT caught — cooperative shutdown, not a tool error.)

**On success:** download the mp4 → the **node** writes `IStorageService` → `brands/{brandId}/assets/{assetId}.mp4` (a retry overwrites the one key — no duplicate, DL-022), then **evicts** `VeoOperationStore.Remove(assetId)` (node-side, AFTER commit, so a storage-failure retry still resumes — no re-bill). Produces `MediaAssetRef { Modality = "video", DurationSec, MimeType = "video/mp4", ... }` onto `RunState.Media` (single ref — no carousel). The brief gains `Modality` + `DurationSec`, stamped deterministically by the Creative Director (`PlatformConstraintValidator.StampVideoFields`) alongside the aspect ratio (R8); `VideoSource` rides on `RunState` (default `ImageSeed`).

**Residual debt (narrowed by the asset-level check).** With asset-level idempotency in place, the only remaining re-bill window is a **cross-process** crash in the **micro-race between `SubmitAsync` returning and `Set(assetId, op)`** (and the same window inside generation before commit). That window is a single in-memory assignment — no I/O, nothing throwable between submit and `Set` — so only a hard crash in those microseconds orphans the op; the retry then submits once more (bounded to one extra job, since the asset won't yet exist). The Veo Developer API exposes **no client-supplied idempotency/request id** on `:predictLongRunning` (confirmed against the docs), so this cannot be closed at the source. The full close is the same as the publish side: a small **durable table** mapping `assetId → operation name + status`, written at submit and read at node entry (cross-process submit-or-resume; ships as an EF migration with RLS if brand-scoped). Recommend deferring unless asked.

## Budget (DL-029)

`MediaBudget` gains a config video price: `Media:VideoPricePerSec` × `DurationSec`. `ImageSeed` also charges the one Nano Banana image. The pre-Media gate (DL-023) checks affordability **before** submitting Veo and degrades to caption-only if unaffordable. Because the node is submit-or-resume, a retry does NOT re-charge. Prices are config-bound and pulled at build time (DL-029) — never hardcoded or recalled from memory.

## CI / eval

`DeterministicMediaGenerationTool` returns a fixed deterministic video asset (no network, no spend). Eval runs (blanked keys, DL-053) must still produce a video `MediaAssetRef` so video runs are scoreable/replayable in Phase 9 exactly like image runs.

## Config-gating

Absent/disabled Veo config must not crash startup or break image runs. Register any Veo health check only when configured (mirror the Vault health-check registration in `HealthCheckRegistration`). The image path must pass `dotnet test` with all Veo config absent.

## Required proofs (Slice A) — shipped

1. **In-process retry resumes, zero new submits** (op-level) — fake `IVeoClient` + submit counter; `VeoVideoGeneratorTests` asserts `SubmitCount == 1` across a simulated node retry (plus a pre-recorded-op resume = 0 submits, and the timeout/terminal throws).
2. **Asset-already-exists → zero submits + zero image generations** (asset-level) — `VideoGenerationTests`: a committed `.mp4` at the deterministic key → `RecordingMediaGenerationTool.Calls == 0`, the existing asset is reused.
3. **Poll timeout → caption-only** AND **terminal Veo error → caption-only** — two separate `VideoGenerationTests` (fake client never finishes / fails terminally); both yield a `ToolError` + a `degraded` span and a caption-only draft at the gate.
4. **Video `MediaBudget` breach → caption-only** draft + a `BudgetDegraded` trace event, zero Veo calls — `VideoGenerationTests`.
5. **End-to-end on `Meta:Mode=mock`** (orchestrator + Testcontainers): `VideoStorageTests` (real mp4 at `brands/{brandId}/assets/{assetId}.mp4` in MinIO, `video/mp4`, `ftyp` signature, assembled video draft) + `VideoPublishTests` (approve → resume → mock publish → `Done`, `mock://meta/` ref).

**Modality selection (shipped):** video is selected on `POST /runs` (`{ modality, videoSource }`, FluentValidation: `videoSource` only with `modality=Video`), persisted on the `AgentRun` row (`modality` NOT NULL default `Image` + nullable `video_source`, EF migration `Dl058RunModalitySelection` — column-add, the `agent_runs` RLS policy is untouched), and read by `ExecuteRun` into `RunState` (a video run → `instagram_reel`/9:16 + a larger flat media budget). **DL-006:** modality travels through the `AgentRun` row, NOT the Hangfire payload (`runId` only), so a retry rebuilds the same modality (proven by `RunModalitySelectionTests`). The `RunState.Modality` string stays the generation-layer form (the `AgentRun.Modality` enum maps to it at the `ExecuteRun` seam) so Slice A generation is untouched. The dashboard toggle + gate `<video>` preview remain a separate frontend pass.
