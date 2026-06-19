using System.Collections.Concurrent;
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
/// <c>creationId → (channel, target, token, caption)</c> in memory. A same-process Hangfire retry
/// recovers from it; only a cross-process restart inside the publish→finalize window loses it (the demo
/// runs one worker process, so the automatic retry is in-process — this never bites the live smoke).</para>
/// </summary>
public sealed class LiveMetaIntegration : IMetaIntegration
{
    private const string ContextMissing =
        "Live publish context unavailable (cross-process restart inside the publish window); re-run the segment.";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly MetaOptions _options;

    // creationId -> the (channel, target, token, caption) the frozen Poll/Publish signatures don't carry.
    private readonly ConcurrentDictionary<string, PublishContext> _contexts = new();

    public LiveMetaIntegration(HttpClient http, IOptions<MetaOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public async Task<ContainerResult> CreateContainerAsync(PublishRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var version = _options.GraphApiVersion;
        var (path, form) = request.Channel switch
        {
            PublishChannel.Instagram => (
                $"{version}/{request.TargetId}/media",
                new Dictionary<string, string> { ["image_url"] = request.MediaUrl, ["caption"] = request.Caption }),
            PublishChannel.FacebookPage => (
                $"{version}/{request.TargetId}/photos",
                new Dictionary<string, string> { ["url"] = request.MediaUrl, ["published"] = "false" }),
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
            _contexts[id] = new PublishContext(request.Channel, request.TargetId, request.AccessToken, request.Caption);
            return new ContainerResult(id, null, null);
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            return new ContainerResult(null, PublishStatus.TransientFailure, "Network error creating container.");
        }
    }

    public async Task<ContainerStatus> PollContainerAsync(PublishChannel channel, string creationId, CancellationToken cancellationToken = default)
    {
        // Facebook unpublished photos need no processing poll — immediate-ready.
        if (channel == PublishChannel.FacebookPage)
        {
            return new ContainerStatus(true, null, null);
        }

        if (!_contexts.TryGetValue(creationId, out var context))
        {
            return new ContainerStatus(false, PublishStatus.TransientFailure, ContextMissing);
        }

        try
        {
            using var message = BuildGet($"{_options.GraphApiVersion}/{creationId}?fields=status_code", context.Token);
            using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var (failure, error) = await ClassifyErrorAsync(response, cancellationToken).ConfigureAwait(false);
                return new ContainerStatus(false, failure, error);
            }

            var status = await response.Content
                .ReadFromJsonAsync<ContainerStatusResponse>(_json, cancellationToken).ConfigureAwait(false);
            return status?.StatusCode switch
            {
                "FINISHED" => new ContainerStatus(true, null, null),
                "IN_PROGRESS" => new ContainerStatus(false, PublishStatus.TransientFailure, "Container still processing."),
                _ => new ContainerStatus(false, PublishStatus.TerminalFailure, $"Container status_code={status?.StatusCode}."),
            };
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            return new ContainerStatus(false, PublishStatus.TransientFailure, "Network error polling container.");
        }
    }

    public async Task<PublishResult> PublishContainerAsync(PublishChannel channel, string creationId, CancellationToken cancellationToken = default)
    {
        if (!_contexts.TryGetValue(creationId, out var context))
        {
            return new PublishResult(PublishStatus.TerminalFailure, null, ContextMissing, null);
        }

        var version = _options.GraphApiVersion;
        var (path, form) = channel switch
        {
            // Re-publishing a committed creation id is server-side deduped by Meta (DL-042): it returns
            // the same media/post id, so the retry recovers the existing ExternalRef rather than posting
            // twice. (Validated by the live smoke — CI never makes this call.)
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

    private readonly record struct PublishContext(PublishChannel Channel, string TargetId, string Token, string Caption);

    // --- Graph response DTOs (snake_case fields need explicit names; web defaults are camelCase) ----

    private sealed record GraphIdResponse(
        [property: JsonPropertyName("id")] string? Id);

    private sealed record ContainerStatusResponse(
        [property: JsonPropertyName("status_code")] string? StatusCode);

    private sealed record GraphErrorEnvelope(
        [property: JsonPropertyName("error")] GraphError? Error);

    private sealed record GraphError(
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("code")] int Code);
}
