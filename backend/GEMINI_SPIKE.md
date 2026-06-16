# Gemini image-generation spike (P3, STEP A)

**Goal:** decide the request/response shape for the real `IMediaGenerationTool` backend by
hitting the live Gemini Developer API (Google AI Studio), then build against what we observe.

**Endpoint / auth (config, never hardcoded):**
- Base URL: `https://generativelanguage.googleapis.com` (`Gemini__BaseUrl`) — the **Developer
  API** surface (AI Studio), *not* Vertex AI (`aiplatform.googleapis.com`).
- Key: `Gemini__ApiKey` from the gitignored `.env` (Vault KV → `IOptions<GeminiOptions>` in prod).
  Current AI-Studio key format is `AQ.`-prefixed (53 chars), not the legacy `AIza`/39-char.
- Auth header: **`x-goog-api-key: <key>`** (confirmed; not a query param). Never logged.
- `GET /v1beta/models` → **HTTP 200**; image models available to the key:
  `gemini-2.5-flash-image`, `gemini-3.1-flash-image`(+preview), `gemini-3-pro-image`(+preview),
  `imagen-4.0-*`. We use the **`gemini-*-flash-image` `generateContent`** path (inline-data
  response), *not* Imagen (`:predict`, different `predictions[].bytesBase64Encoded` shape).

## Request shape (image generation)

```
POST {BaseUrl}/{ApiVersion}/models/{Model}:generateContent
x-goog-api-key: <key>
Content-Type: application/json

{
  "contents": [{ "parts": [{ "text": "<MediaPromptBrief rendered to a prompt>" }] }],
  "generationConfig": {
    "responseModalities": ["IMAGE"],
    "imageConfig": { "aspectRatio": "4:5" }   // aspect ratio from the CD-stamped brief (R8)
  }
}
```

- `Model` default `gemini-2.5-flash-image`, `ApiVersion` default `v1beta` — both config-bound
  (`GeminiOptions`), swappable to `gemini-3.1-flash-image` / `gemini-3-pro-image` without code change.

## Response shape (success) — maps cleanly to `MediaResult(byte[] Bytes, string MimeType)`

```json
{
  "candidates": [{
    "content": { "parts": [
      { "inlineData": { "mimeType": "image/png", "data": "<BASE64 image bytes>" } }
    ]}
  }]
}
```

Parse: first `candidates[].content.parts[]` with `inlineData` →
`MediaResult(Convert.FromBase64String(inlineData.data), inlineData.mimeType)`. **No record/schema
change** — the STEP-A STOP-gate (response shape vs `MediaAssetRef` path) did **not** trip.

> **Live success body: PENDING a billing-enabled key.** The success shape above is the documented
> one; the real body is captured by the key-gated `LiveGemini` test (STEP C) once the project has
> image quota (see below). Section updated with the observed `mimeType` + byte length then.

## Error shapes observed

- **`429 RESOURCE_EXHAUSTED` — free-tier image quota is 0/day (observed, definitive).** Every image
  model (`gemini-2.5-flash-image` → `…-preview-image`, `gemini-3.1-flash-image`, `gemini-3-pro-image`)
  returns 429 even on a **single** request after a multi-minute gap. The structured `QuotaFailure`
  names the quota:
  ```
  quotaId:     GenerateRequestsPerDayPerProjectPerModel-FreeTier
  quotaMetric: generativelanguage.googleapis.com/generate_content_free_tier_requests   limit: 0
               generativelanguage.googleapis.com/generate_content_free_tier_input_token_count limit: 0
  ```
  This is a **per-day** free-tier limit of **zero** for the image models — i.e. image generation is
  **not on the free tier** for this project; it needs **billing enabled** (paid tier) or a paid key.
  The `"Please retry in ~23s"` text is Google's generic backoff hint and is **not** a real per-minute
  window here (retrying does not help a 0/day limit). No `Retry-After` header was returned.
  ```json
  { "error": { "code": 429, "status": "RESOURCE_EXHAUSTED",
               "message": "You exceeded your current quota ... limit: 0, model: gemini-2.5-flash-preview-image" } }
  ```
- **4xx (e.g. 400 invalid request, 401/403 bad key)**: non-transient client errors.

> **Live round-trip status: PENDING.** The integration is built and unit-exercised on the mock; the
> real image round-trip is blocked only by the project's free-tier image quota (0/day). Enable
> billing on the Google project (or supply a paid-tier key) and re-run the `LiveGemini` test — zero
> code change. Until then the test logs `PENDING` on a 429 rather than failing.

## Resilience decision (mapped in the tool's Polly policy; node boundary unchanged)

| Outcome | Class | Action |
|---|---|---|
| `429 RESOURCE_EXHAUSTED` | **transient** (per-minute rate limit) — *or* daily quota exhausted | **retry** with bounded backoff, honoring `Retry-After` if present (else exponential); a 0/day free-tier limit falls through retries to the fail-item below |
| `5xx` / `408` / network / per-try timeout | transient | retry with backoff |
| other `4xx` (400/401/403/404) | permanent | **no retry** — fail fast |
| retries exhausted | — | tool throws → Media node → structured `ToolError` (`media.generation_failed`), fail-item (DL-023). Never an exception into the graph (DL-022) |

Notes: a single run generates exactly **one** image (≤ 10 RPM); cross-run rate-limit queueing is
banked (no MVP orchestration change). CI never makes a live call (`Gemini:Mode=mock`), so neither
the daily nor the per-minute limit ever touches the test suite. The Media **node** owns the
deterministic `assetId` + MinIO write (idempotent on retry, DL-022) — the tool returns bytes only.
