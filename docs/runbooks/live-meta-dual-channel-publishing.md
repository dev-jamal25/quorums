# Runbook — live dual-channel publishing (Meta:Mode=live, DL-055)

Light up the **real** Instagram + Facebook Page publish for one approved content item, then turn it back
off. The default path is the network-free mock; this is a one-off manual smoke, **never** run in CI
(DL-051 — no live spend). Everything here is operator-side; no secret is ever committed.

**Prerequisites:** a Meta **dev-mode app** on accounts you own — a Facebook **Page**, an **Instagram
Business** account linked to that Page, and a **long-lived Page access token** with
`pages_manage_posts`, `pages_read_engagement`, `instagram_basic`, `instagram_content_publish`. (Dev-mode
on owned accounts needs no app review; the documented production path is app review + Advanced Access.)
The stack is up (`docker compose up`) and migrations are applied.

> The token is the only secret. It is supplied **transiently** via `META_PAGE_TOKEN`, Transit-encrypted
> before it touches the DB, and stored as ciphertext in `BrandMetaConnection`. Never paste it into a
> file, a CLI arg, or a log. Rotate it after the smoke (step 7).

## 1. Start a public tunnel to MinIO's asset origin

Meta fetches the image **server-side**, so the asset URL must be public (not `localhost`). Tunnel the
public asset origin (the MinIO endpoint / a read-proxy that serves `media/brands/...`):

```bash
cloudflared tunnel --url http://localhost:9000
# note the assigned host, e.g. https://random-words.trycloudflare.com
```

## 2. Point Storage:PublicBaseUrl at the tunnel host

Set it so the node builds `MediaUrl = {PublicBaseUrl}/brands/{brand_id}/assets/{asset_id}.png`:

```bash
Storage__PublicBaseUrl=https://random-words.trycloudflare.com
```

(Set it in the api/worker environment and recreate them, or export before `dotnet run`.)

## 3. Make the asset bucket public-read

Meta fetches anonymously, so the `media` bucket (or at least the `brands/` prefix) must be public-read:

```bash
mc anonymous set download local/media     # or: set a read-only bucket policy on the brands/ prefix
```

Verify: `curl -I {PublicBaseUrl}/brands/<brand_id>/assets/<asset_id>.png` returns `200`.

## 4. Seed the brand's live Meta connection

Transient token in env, target ids as args. This Transit-encrypts the token and upserts
`BrandMetaConnection` (ciphertext + Page id + IG Business Account id):

```bash
read -rs META_PAGE_TOKEN; export META_PAGE_TOKEN          # paste the long-lived Page token; not echoed
dotnet Backend.Api.dll meta-connect \
    --brand <BRAND_ID> \
    --page-id <FACEBOOK_PAGE_ID> \
    --ig-id  <IG_BUSINESS_ACCOUNT_ID>
unset META_PAGE_TOKEN
```

Connecting both ids makes the brand a **dual-channel** target. Connect only `--page-id` or only `--ig-id`
to publish to a single channel.

## 5. Switch Meta to live and recreate api/worker

```bash
Meta__Mode=live
# recreate the api + worker so the live LiveMetaIntegration (typed HttpClient + Polly) is resolved
docker compose up -d --no-deps api worker
```

`Meta:Mode` stays `mock` everywhere else; only this environment is live.

## 6. Run one content item through the gate and verify

1. Trigger a run for `<BRAND_ID>` (dashboard or `POST /runs`), let it reach **AwaitingApproval**.
2. Approve at the gate (`POST /runs/{id}/approval`, `approve`).
3. `ResumeRun` publishes each connected channel as its own `(contentItemId, channel)` unit.
4. Confirm a **real** id came back per channel — `GET /runs/{id}/trace` and the `PublishRecord` rows:

```bash
# one finalized PublishRecord per (contentItemId, channel), each with a real ExternalRef:
#   Instagram    -> ExternalRef = <real IG media id>
#   FacebookPage -> ExternalRef = <real FB Page post id>
```

Paste the two real ids into the slice's live-smoke note (redact the token). Cross-check the posts appear
on the Instagram account and the Facebook Page.

> **Crash-recovery caveat (flagged):** the live re-publish recovery (DL-042) for a committed
> `CreationId` relies on an in-memory `creationId → (channel, target, token, caption)` capture, because
> the frozen `Poll`/`Publish(channel, creationId)` signatures don't carry the token/target. A same-process
> Hangfire retry recovers from it; only a **cross-process worker restart inside the publish→finalize
> window** loses it — re-run the segment in that rare case. The single-worker demo never hits this.

## 7. Rotate the token and turn live off

After the smoke, **revoke/rotate** the Page token in the Meta app and put the path back to mock:

```bash
Meta__Mode=mock
Storage__PublicBaseUrl=
# re-seed the connection with a fresh token if you keep the brand, or leave it revoked:
#   read -rs META_PAGE_TOKEN; export META_PAGE_TOKEN; dotnet Backend.Api.dll meta-connect --brand <ID> ...; unset META_PAGE_TOKEN
docker compose up -d --no-deps api worker
```

Stop the tunnel and drop the public bucket policy (`mc anonymous set none local/media`). CI and the
default demo remain mock-only — zero live Meta calls, zero spend.
