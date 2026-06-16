# IMetaIntegration contract, failure taxonomy, idempotency, retry (DL-038, DL-039)

`IMetaIntegration` is shaped for the **real** Instagram content-publish so `LiveMetaIntegration` is a true drop-in. The MVP ships `MockMetaIntegration`, which must honor this shape — not a one-call passthrough. (Angle brackets below are required C# generic syntax inside code blocks only.)

## The two-step publish (real shape)

Instagram content publishing is: **create media container → poll status until processed → publish container → obtain the published media id (the externalRef).** Model the contract on this; the Publishing node orchestrates it.

```csharp
public interface IMetaIntegration
{
    Task<PublishResult> PublishAsync(PublishRequest request, CancellationToken ct);
}

public record PublishRequest(
    Guid ContentItemId,                  // idempotency key
    PostSurface Surface,                 // image/video maps to feed/reel/story (modality-aware)
    string MediaUrl,                     // brand-scoped MinIO URL
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

public enum PublishStatus { Published, TransientFailure, TerminalFailure }

public record EngagementKeys(string MediaId, string? Permalink);  // shape now, populate when live
```

## Pre-publish idempotency guard (DL-022/039) — MANDATORY

The two-step publish is **not naturally idempotent**: if `publish container` succeeds but the job crashes before the result is recorded, a Hangfire retry re-runs create+publish and double-posts.

Before the publish step, the Publishing node MUST check whether `ContentItemId` already has a recorded published media id:

```csharp
// The persisted PublishRecord (see audit-schema.md) is the source of truth.
if (publishStore.TryGetExternalRef(contentItemId, out var existing))
    return PublishResult.AlreadyPublished(existing);   // skip; no second post
```

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

1. Model the **two-step shape** (create → poll → publish), not a single call.
2. Be able to **simulate a crash between publish and result-record** so the idempotency test is real (an injectable failure point after the publish step succeeds).
3. **Inject both `TransientFailure` and `TerminalFailure` deterministically** (config or seam-driven) so the retry path and the surface path are both tested without live Meta.
4. Produce a stable `ExternalRef` (`mock://meta/{DeterministicGuid(runId,"meta")}`) across retries.

`LiveMetaIntegration` is a present-but-throwing seam unless `Meta:Mode=live`; it implements the same interface with the real Graph API calls.
