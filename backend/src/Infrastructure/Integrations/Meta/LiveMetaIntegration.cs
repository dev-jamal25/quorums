using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Core.Domain;
using Backend.Core.Integrations;
using Backend.Core.Orchestration.Contracts;
using Backend.Infrastructure.Configuration.Options;
using Microsoft.Extensions.Options;
using Polly.Timeout;

namespace Backend.Infrastructure.Integrations.Meta;

/// <summary>
/// Live Meta publisher (selected by <c>Meta:Mode=live</c>; mock stays the CI/default, DL-055). One
/// channel-aware client over the real Graph API for BOTH channels, honoring the frozen two-step shape:
/// <list type="bullet">
///   <item><b>Instagram</b>: <c>POST /{ig-user-id}/media</c> (image_url+caption) → container id; poll
///   <c>GET /{creation-id}?fields=status_code</c> to <c>FINISHED</c>; <c>POST /{ig-user-id}/media_publish</c>
///   (creation_id) → media id.</item>
///   <item><b>Facebook Page</b>: <c>POST /{page-id}/photos</c> (url, published=false) → unpublished
///   photo id; immediate-ready (no processing poll); <c>POST /{page-id}/feed</c> (attached_media +
///   message) → page-post id.</item>
/// </list>
/// The PNG is sent as-is (no JPEG conversion); the caption's <c>#</c> is encoded as <c>%23</c>
/// automatically by <see cref="FormUrlEncodedContent"/>. The per-brand token is passed as an
/// <c>Authorization: Bearer</c> header (never in a URL or a log) and used only at the call.
/// <para><b>Typed classification (never exception-sniffing):</b> every outcome is mapped to a
/// <see cref="PublishStatus"/> from the HTTP result; transient HTTP (5xx/408/network/per-attempt
/// timeout) and 429 are first absorbed by the client's Polly retry, then anything still failing is
/// classified (5xx/408/network → transient for the Hangfire retry; 4xx/429-exhausted → terminal).</para>
/// <para><b>Live recovery seam (DL-042, flagged for the live smoke):</b> the frozen
/// <c>Poll</c>/<c>Publish(channel, creationId)</c> signatures carry no token/target, so create captures
/// <c>creationId → (channel, target, token, caption)</c> in the singleton
/// <see cref="LivePublishContextStore"/>. Because this client is a <b>transient</b> typed
/// <c>HttpClient</c>, the store MUST outlive the instance — a Hangfire retry resolves a fresh client but
/// shares the store, so it recovers the committed container (e.g. after an Instagram "container still
/// processing" poll). Only a true cross-process worker restart loses the store (the documented limit).</para>
/// </summary>
public sealed class LiveMetaIntegration : IMetaIntegration
{
    private const string ContextMissing =
        "Live publish context unavailable (cross-process restart inside the publish window); re-run the segment.";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly MetaOptions _options;

    // Singleton store of the (channel, target, token, caption) the frozen Poll/Publish signatures don't
    // carry — shared across the transient client instances a Hangfire retry resolves.
    private readonly LivePublishContextStore _contexts;

    public LiveMetaIntegration(HttpClient http, IOptions<MetaOptions> options, LivePublishContextStore contexts)
    {
        _http = http;
        _options = options.Value;
        _contexts = contexts;
    }

    public async Task<ContainerResult> CreateContainerAsync(PublishRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Branch on (channel, modality) — DL-058 adds the video surfaces (IG Reel, FB Page video) to the
        // image surfaces. A video run maps to PostSurface.Reel for both channels (DL-030); the image path
        // is unchanged. IG video is the SAME container/poll/publish shape as IG image (just media_type +
        // video_url), so only create differs; FB video uses /videos (file_url) + a processing poll.
        var version = _options.GraphApiVersion;
        var isVideo = request.Surface.IsVideo();
        var (path, form) = (request.Channel, isVideo) switch
        {
            (PublishChannel.Instagram, false) => (
                $"{version}/{request.TargetId}/media",
                new Dictionary<string, string> { ["image_url"] = request.MediaUrl, ["caption"] = request.Caption }),
            (PublishChannel.Instagram, true) => (
                $"{version}/{request.TargetId}/media",
                new Dictionary<string, string>
                {
                    ["media_type"] = "REELS",
                    ["video_url"] = request.MediaUrl,
                    ["caption"] = request.Caption,
                }),
            (PublishChannel.FacebookPage, false) => (
                $"{version}/{request.TargetId}/photos",
                new Dictionary<string, string> { ["url"] = request.MediaUrl, ["published"] = "false" }),
            (PublishChannel.FacebookPage, true) => (
                $"{version}/{request.TargetId}/videos",
                new Dictionary<string, string>
                {
                    ["file_url"] = request.MediaUrl,
                    ["description"] = request.Caption,
                    // No published=false: /videos posts the Page video itself (default published). It is NOT
                    // an unpublished container to attach to /feed (that's photos). The returned video id is
                    // committed as the CreationId; poll status → publish step is a no-op (DL-058).
                }),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Channel, "Unknown channel."),
        };

        try
        {
            using var message = BuildPost(path, form, request.AccessToken);
            using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            var (id, failure, error) = await ReadIdAsync(response, cancellationToken).ConfigureAwait(false);
            if (id is null)
            {
                return new ContainerResult(null, failure, error);
            }

            // Capture what poll/publish will need (the signatures carry only channel + creationId).
            _contexts.Set(id, new LivePublishContext(
                request.Channel, request.TargetId, request.AccessToken, request.Caption, request.Surface));
            return new ContainerResult(id, null, null);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            return new ContainerResult(null, PublishStatus.TransientFailure, "Network error creating container.");
        }
    }

    public async Task<ContainerStatus> PollContainerAsync(PublishChannel channel, string creationId, CancellationToken cancellationToken = default)
    {
        var hasContext = _contexts.TryGet(creationId, out var context);
        var isVideo = hasContext && context.Surface.IsVideo();

        // FB photo is immediate-ready and needs no context (preserves cross-process behavior).
        if (channel == PublishChannel.FacebookPage && !isVideo)
        {
            return new ContainerStatus(true, null, null);
        }

        // IG (image or reel) and FB video all need the recovered token from the singleton context.
        if (!hasContext)
        {
            return new ContainerStatus(false, PublishStatus.TransientFailure, ContextMissing);
        }

        // VIDEO (IG reel / FB video): transcoding takes minutes — far longer than the Hangfire retry
        // budget. The post already exists on Meta's side once create ran, so poll GENEROUSLY in-call until
        // ready (up to Meta:VideoPollTimeout) and record the outcome accurately, instead of giving up early
        // and marking a live post Failed (DL-058 — the "generous bounded poll"). Image polls once.
        if (isVideo)
        {
            var deadline = DateTimeOffset.UtcNow + _options.VideoPollTimeout;
            while (true)
            {
                var status = await PollOnceAsync(channel, creationId, context, cancellationToken).ConfigureAwait(false);
                if (status.Processed || status.Failure == PublishStatus.TerminalFailure)
                {
                    return status;
                }

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return status; // generous bound hit, still processing → transient (a Hangfire retry re-enters)
                }

                var wait = _options.VideoPollInterval < remaining ? _options.VideoPollInterval : remaining;
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }

        // IG IMAGE: a single status_code poll (image processing is fast; the Hangfire budget suffices).
        return await PollOnceAsync(channel, creationId, context, cancellationToken).ConfigureAwait(false);
    }

    // One poll of the container's processing status — FB reads status.video_status, IG reads status_code.
    // Only an explicit error state is terminal; ready/FINISHED is processed; ANY other (in-progress or
    // unfamiliar) value is transient so the video loop keeps waiting rather than false-failing a live post.
    private async Task<ContainerStatus> PollOnceAsync(
        PublishChannel channel, string creationId, LivePublishContext context, CancellationToken cancellationToken)
    {
        try
        {
            var fields = channel == PublishChannel.FacebookPage ? "status" : "status_code";
            using var message = BuildGet($"{_options.GraphApiVersion}/{creationId}?fields={fields}", context.Token);
            using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var (failure, error) = await ClassifyErrorAsync(response, cancellationToken).ConfigureAwait(false);
                return new ContainerStatus(false, failure, error);
            }

            if (channel == PublishChannel.FacebookPage)
            {
                var fb = await response.Content
                    .ReadFromJsonAsync<VideoStatusResponse>(_json, cancellationToken).ConfigureAwait(false);
                return fb?.Status?.VideoStatus switch
                {
                    "ready" => new ContainerStatus(true, null, null),
                    "error" => new ContainerStatus(false, PublishStatus.TerminalFailure, "FB video processing failed."),
                    _ => new ContainerStatus(false, PublishStatus.TransientFailure, $"FB video processing ({fb?.Status?.VideoStatus ?? "pending"})."),
                };
            }

            var ig = await response.Content
                .ReadFromJsonAsync<ContainerStatusResponse>(_json, cancellationToken).ConfigureAwait(false);
            return ig?.StatusCode switch
            {
                "FINISHED" => new ContainerStatus(true, null, null),
                "ERROR" or "EXPIRED" => new ContainerStatus(false, PublishStatus.TerminalFailure, $"Container status_code={ig?.StatusCode}."),
                _ => new ContainerStatus(false, PublishStatus.TransientFailure, $"Container processing ({ig?.StatusCode ?? "pending"})."),
            };
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            return new ContainerStatus(false, PublishStatus.TransientFailure, "Network error polling container.");
        }
    }

    public async Task<PublishResult> PublishContainerAsync(PublishChannel channel, string creationId, CancellationToken cancellationToken = default)
    {
        if (!_contexts.TryGet(creationId, out var context))
        {
            return new PublishResult(PublishStatus.TerminalFailure, null, ContextMissing, null);
        }

        // FB VIDEO is ALREADY posted by POST /{page-id}/videos at the create step — a Page video is its own
        // post type, NOT an unpublished container you attach to /feed via media_fbid like a photo (DL-058,
        // live-confirmed: /feed attach works for FB images but not videos). So the publish step is a no-op
        // that finalizes with the committed video id. (The create-window double-post is the documented
        // deferred debt; a crash-in-publish-window retry now re-enters here and is a clean no-op.)
        if (channel == PublishChannel.FacebookPage && context.Surface.IsVideo())
        {
            return new PublishResult(PublishStatus.Published, creationId, null, new EngagementKeys(creationId, null));
        }

        // IG publishes the committed container (image feed OR reel) via media_publish; FB PHOTO attaches the
        // committed unpublished photo (media_fbid) to a feed post. For IMAGE, re-publishing a committed
        // creation id is server-side deduped by Meta (DL-042), so a crash-in-publish-window retry recovers
        // the same id rather than posting twice. (IG reel re-media_publish errors — inherited deferred debt.
        // Validated by the live smoke — CI never makes this call.)
        var version = _options.GraphApiVersion;
        var (path, form) = channel switch
        {
            PublishChannel.Instagram => (
                $"{version}/{context.TargetId}/media_publish",
                new Dictionary<string, string> { ["creation_id"] = creationId }),
            PublishChannel.FacebookPage => (
                $"{version}/{context.TargetId}/feed",
                new Dictionary<string, string>
                {
                    ["attached_media"] = $"[{{\"media_fbid\":\"{creationId}\"}}]",
                    ["message"] = context.Caption,
                }),
            _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown channel."),
        };

        try
        {
            using var message = BuildPost(path, form, context.Token);
            using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            var (id, failure, error) = await ReadIdAsync(response, cancellationToken).ConfigureAwait(false);
            if (id is null)
            {
                return new PublishResult(failure ?? PublishStatus.TerminalFailure, null, error, null);
            }

            return new PublishResult(PublishStatus.Published, id, null, new EngagementKeys(id, null));
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            return new PublishResult(PublishStatus.TransientFailure, null, "Network error publishing container.", null);
        }
    }

    // --- Graph HTTP helpers -------------------------------------------------------------------------

    // Token rides the Authorization header (never the URL or a log); form bodies percent-encode the
    // caption, so a hashtag '#' becomes '%23' at the boundary exactly as the contract requires.
    private static HttpRequestMessage BuildPost(string path, IReadOnlyDictionary<string, string> form, string token)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(form),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return message;
    }

    private static HttpRequestMessage BuildGet(string path, string token)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, path);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return message;
    }

    private static async Task<(string? Id, PublishStatus? Failure, string? Error)> ReadIdAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var (failure, error) = await ClassifyErrorAsync(response, cancellationToken).ConfigureAwait(false);
            return (null, failure, error);
        }

        var payload = await response.Content
            .ReadFromJsonAsync<GraphIdResponse>(_json, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrEmpty(payload?.Id)
            ? (null, PublishStatus.TerminalFailure, "Graph response carried no id.")
            : (payload.Id, null, null);
    }

    private static async Task<(PublishStatus Failure, string Error)> ClassifyErrorAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Polly already absorbed in-call transient/429 retries; classify what still failed.
        var failure = response.StatusCode switch
        {
            HttpStatusCode.RequestTimeout => PublishStatus.TransientFailure,
            HttpStatusCode.TooManyRequests => PublishStatus.TerminalFailure,  // rate limit exhausted
            >= HttpStatusCode.InternalServerError => PublishStatus.TransientFailure,
            _ => PublishStatus.TerminalFailure,                               // 4xx (auth/policy/invalid)
        };

        string detail;
        try
        {
            var envelope = await response.Content
                .ReadFromJsonAsync<GraphErrorEnvelope>(_json, cancellationToken).ConfigureAwait(false);
            detail = envelope?.Error is { } error
                ? $"Graph error {error.Code}: {error.Message}"
                : $"Graph HTTP {(int)response.StatusCode}.";
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException)
        {
            detail = $"Graph HTTP {(int)response.StatusCode}.";
        }

        return (failure, detail);
    }

    private static bool IsTransient(Exception ex) => ex is HttpRequestException or TimeoutRejectedException;

    // --- Graph response DTOs (snake_case fields need explicit names; web defaults are camelCase) ----

    private sealed record GraphIdResponse(
        [property: JsonPropertyName("id")] string? Id);

    private sealed record ContainerStatusResponse(
        [property: JsonPropertyName("status_code")] string? StatusCode);

    // FB Page video processing status: GET /{video-id}?fields=status → { "status": { "video_status": ... } }.
    private sealed record VideoStatusResponse(
        [property: JsonPropertyName("status")] FbVideoStatus? Status);

    private sealed record FbVideoStatus(
        [property: JsonPropertyName("video_status")] string? VideoStatus);

    private sealed record GraphErrorEnvelope(
        [property: JsonPropertyName("error")] GraphError? Error);

    private sealed record GraphError(
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("code")] int Code);
}
