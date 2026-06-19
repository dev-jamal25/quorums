# IMetaIntegration contract, failure taxonomy, idempotency, retry (DL-038, DL-039, DL-055)

`IMetaIntegration` is shaped for the **real** Instagram + Facebook Page content-publish (dual-channel, DL-055) so `LiveMetaIntegration` is a true drop-in. The MVP ships `MockMetaIntegration`, which must honor this shape — not a one-call passthrough. (Angle brackets below are required C# generic syntax inside code blocks only.)

**Channel-aware (DL-055).** A `PublishChannel` on the request selects the surface; the integration
branches Instagram vs Facebook Page internally, and idempotency keys on `(ContentItemId, Channel)`.

## The two-step publish (real shape)

Instagram content publishing is: **create media container → poll status until processed → publish container → obtain the published media id (the externalRef).** Model the contract on this; the Publishing node orchestrates it.

The steps are exposed SEPARATELY so the publish-execution component can persist the container
`CreationId` BETWEEN create and publish (a single opaque call cannot — see the idempotency section).

```csharp
public interface IMetaIntegration
{
    Task<ContainerResult> CreateContainerAsync(PublishRequest request, CancellationToken ct);  // request.Channel selects IG vs Facebook Page
    Task<ContainerStatus>  PollContainerAsync(PublishChannel channel, string creationId, CancellationToken ct);
    Task<PublishResult>    PublishContainerAsync(PublishChannel channel, string creationId, CancellationToken ct);  // idempotent on creationId
}

public record ContainerResult(string? CreationId, PublishStatus? Failure, string? Error);   // CreationId set = created
public record ContainerStatus(bool Processed, PublishStatus? Failure, string? Error);

public record PublishRequest(
    Guid ContentItemId,                  // idempotency key (with Channel)
    PublishChannel Channel,              // Instagram | FacebookPage (DL-055)
    string TargetId,                     // IG Business Account ID (IG) | Page ID (Facebook); resolved from BrandMetaConnection
    PostSurface Surface,                 // image/video maps to feed/reel/story (modality-aware)
    string MediaUrl,                     // Meta-reachable PUBLIC URL {Storage:PublicBaseUrl}/brands/{brand_id}/assets/{asset_id} (DL-055); Meta fetches it server-side - never a localhost URL
    string Caption,
    IReadOnlyList<string> Hashtags,
    string AccessToken                   // decrypted per-brand token (Vault Transit); never logged
);

public record PublishResult(
    PublishStatus Status,
    string? ExternalRef,                 // published media id; set when Published
    string? Error,                       // failure detail; surfaced to the reviewer on Terminal
    EngagementKeys? EngagementKeys       // for the Analytics agent to poll later (Phase 7)
);

public enum PublishStatus  { Published, TransientFailure, TerminalFailure }
public enum PublishChannel { Instagram, FacebookPage }   // DL-055

public record EngagementKeys(string MediaId, string? Permalink);  // shape now, populate when live
```

## Per-channel mechanics (DL-055)

One channel-aware integration, the same three steps, branching on `request.Channel` / the `channel` arg.
`CreationId` is the created-but-unpublished unit on each surface; `ExternalRef` is the live post id.

- **Instagram** (image → feed): `CreateContainerAsync` = `POST /{TargetId=ig-user-id}/media`
  (`image_url`+`caption`) → `CreationId` = container id; `PollContainerAsync` = poll
  `GET /{creationId}?fields=status_code` until `FINISHED` (still processing → `TransientFailure`);
  `PublishContainerAsync` = `POST /{ig-user-id}/media_publish?creation_id=…` → `ExternalRef` = media id.
- **Facebook Page**: `CreateContainerAsync` = `POST /{TargetId=page-id}/photos?published=false` (`url`)
  → `CreationId` = unpublished photo id; `PollContainerAsync` = immediate-ready (Facebook photos need no
  processing poll — return `Processed=true`); `PublishContainerAsync` = `POST /{page-id}/feed` with
  `attached_media=[{media_fbid: creationId}]` (+`message`) → `ExternalRef` = page-post id.

Both inherit the create→persist-`CreationId`→publish guard below unchanged; only the endpoints differ.
The single published `POST /{page-id}/photos` form posts in one call but reopens the crash-after-send
double-post window — do NOT use it on the build path; use the unpublished two-step.

**Image format / IG validation.** Send the generated **PNG as-is** — Instagram accepts it (verified
end-to-end against live Meta; Meta docs say JPEG-only but the API accepts PNG) and Facebook `/photos`
accepts PNG natively; **no JPEG conversion.** Enforce IG's input rules at the publish boundary: image
**aspect ratio between 4:5 and 1.91:1** (square is fine), and any `#` in a caption **URL-encoded as
`%23`**. A rejected format/aspect returns container `status_code=ERROR` → `TerminalFailure` (already in
the taxonomy), surfaced to the reviewer.

## Robust creation-id idempotency (DL-022/039) — MANDATORY

The two-step publish is **not naturally idempotent**: if `publish container` succeeds but the job
crashes before the result is recorded, a Hangfire retry re-runs create+publish and double-posts.
A guard that only checks for an existing `ExternalRef` does NOT close this window — after the crash
there is no record, so the retry re-publishes.

The fix is to persist the container **`CreationId` immediately after create, BEFORE publish**, each
write **committed in its own brand-scoped unit** (the brand GUC is transaction-local and a single long
transaction would lose the CreationId on a mid-publish crash). The persisted `PublishRecord` (keyed on
`(ContentItemId, Channel)`, see `audit-schema.md`) is the source of truth; re-entry keys on its state:

```csharp
var record = await store.FindByContentItemAndChannelAsync(contentItemId, request.Channel);   // RLS-scoped read

// (c) finalized → skip; no second post.
if (record?.ExternalRef is not null)
    return PublishResult.Published(record.ExternalRef, record.EngagementKeys);

// (b) container already created but publish not recorded → re-publish the SAME container (Meta dedups).
//     (a) nothing yet → create, then persist the CreationId BEFORE publishing.
var creationId = record?.CreationId
    ?? await PersistCreationAsync(await meta.CreateContainerAsync(request), request.Channel);   // committed before publish

// poll until processed, then publish; the publish step is idempotent on creationId.
var result = await meta.PublishContainerAsync(request.Channel, creationId);
await FinalizeAsync(contentItemId, request.Channel, result);   // commit ExternalRef + Status=Published, keyed (ContentItemId, Channel)
return result;
```

A crash in the create→persist-`CreationId` window leaves only an orphan unpublished container
(harmless); a crash after publish but before finalize is recovered by re-publishing the committed
`CreationId`. Either way: exactly one published media.

## Failure taxonomy — classify from the typed result, never exception-sniff

| Real condition | Status | Handling |
|---|---|---|
| Network timeout, connection reset | TransientFailure | retry |
| HTTP 5xx | TransientFailure | retry |
| Rate limit WITH retry-after | TransientFailure | retry (respect backoff) |
| Media container still processing on poll | TransientFailure | retry the poll |
| Auth/token revoked or expired | TerminalFailure | surface to human (re-auth) |
| Content rejected by policy | TerminalFailure | surface (edit content) |
| Invalid media / unsupported format | TerminalFailure | surface |
| Rate limit EXHAUSTED, no retry-after | TerminalFailure | surface |
| Account restricted or disabled | TerminalFailure | surface |

## Retry policy (DL-039)

- **Transient** → Hangfire automatic retry on the resume/publish job: **3 attempts, exponential backoff.** Each attempt re-enters through the idempotency guard, so a mid-publish crash never double-posts.

```csharp
[AutomaticRetry(Attempts = 3, DelaysInSeconds = new[] { 10, 30, 90 })]
public Task ResumeRun(Guid runId) { /* publish path */ }
```

- **Terminal** → **0 retries.** Emit a `ToolError` onto `RunState.Errors`, set `AgentRun` to `Failed`, surface `PublishResult.Error` to the reviewer.

## Mock requirements (CI must exercise both paths)

`MockMetaIntegration` must:

1. Model the **separate steps** (`CreateContainerAsync` → `PollContainerAsync` → `PublishContainerAsync`) for **both channels** (Instagram container two-step and Facebook unpublished-photo two-step), not a single call, with a **fresh creation id per create** (so a crashed create leaves a real orphan).
2. Make a **re-publish of an already-published container deduped to the same media id** (Meta's server-side idempotency).
3. Expose **injectable crash points in BOTH durability windows** — after-create-before-persist-`CreationId` AND after-publish-before-record — so both idempotency paths are tested.
4. **Inject both `TransientFailure` and `TerminalFailure` deterministically** (returned typed, not thrown) so the retry path and the surface path are both tested without live Meta.
5. Produce a stable `ExternalRef` (`mock://meta/{channel}/{DeterministicGuid(contentItemId, channel, "meta")}`) keyed on the **(contentItemId, channel)** idempotency key, across retries — a distinct ref per channel.

`LiveMetaIntegration` is a present-but-throwing seam unless `Meta:Mode=live`; it implements the same interface with the real Graph API calls for **both** channels (the per-channel mechanics above). It runs against a **dev-mode** Meta app on owned accounts (no app review) — proven end-to-end; the documented production path is app review + Advanced Access for non-owned accounts and automated token refresh.
