using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Core.Integrations;
using Backend.Infrastructure.Configuration.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backend.Infrastructure.Integrations.Gemini;

/// <summary>
/// Real Veo 3.1 backend behind <see cref="IVeoClient"/> (registered only when <c>Veo:Mode=live</c>;
/// the deterministic tool serves CI/mock). It speaks the Gemini Developer-API long-running-operation
/// shape (DL-058): <c>POST {ApiVersion}/models/{model}:predictLongRunning</c> returns an operation
/// <c>name</c>; <c>GET {ApiVersion}/{name}</c> polls until <c>done</c> and exposes the result at
/// <c>response.generateVideoResponse.generatedSamples[0].video.uri</c>; the uri downloads the mp4. The
/// typed <see cref="HttpClient"/> carries the base address, the <c>x-goog-api-key</c> header (the SAME
/// Gemini key as Nano Banana, from Vault — never on the command line, in a URL, or logged), and the
/// transient/429 retry policy (wired in <c>AddGeneration</c>). The bounded poll loop + submit-or-resume
/// idempotency live in <see cref="VeoVideoGenerator"/>; this client is a thin, stateless wire mapper.
/// </summary>
public sealed partial class LiveVeoClient : IVeoClient
{
    private const int ErrorBodyLogLimit = 500;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _apiVersion;
    private readonly ILogger<LiveVeoClient> _logger;

    public LiveVeoClient(HttpClient http, IOptions<GeminiOptions> gemini, ILogger<LiveVeoClient> logger)
    {
        _http = http;
        _apiVersion = gemini.Value.ApiVersion;
        _logger = logger;
    }

    public async Task<string> SubmitAsync(VeoSubmitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var instance = new VeoInstance(
            Prompt: request.Prompt,
            Image: request.SeedImage is { } seed
                ? new VeoImage(new VeoInlineData(seed.MimeType, Convert.ToBase64String(seed.Bytes)))
                : null);
        var body = new VeoGenerateRequest(
            Instances: [instance],
            Parameters: new VeoParameters(request.AspectRatio, request.DurationSec));

        var path = $"{_apiVersion}/models/{request.Model}:predictLongRunning";
        LogSubmitting(request.Model, request.AspectRatio, request.DurationSec, request.SeedImage is not null);

        using var response = await _http.PostAsJsonAsync(path, body, _json, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "predictLongRunning", cancellationToken).ConfigureAwait(false);

        var payload = await response.Content
            .ReadFromJsonAsync<VeoOperationResponse>(_json, cancellationToken).ConfigureAwait(false);
        if (payload is null || string.IsNullOrWhiteSpace(payload.Name))
        {
            throw new VeoGenerationException("Veo predictLongRunning returned no operation name.");
        }

        return payload.Name;
    }

    public async Task<VeoOperation> PollAsync(string operationName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);

        var path = $"{_apiVersion}/{operationName.TrimStart('/')}";
        using var response = await _http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "operations.get", cancellationToken).ConfigureAwait(false);

        var payload = await response.Content
            .ReadFromJsonAsync<VeoOperationResponse>(_json, cancellationToken).ConfigureAwait(false);

        if (payload is null || payload.Done != true)
        {
            return new VeoOperation(VeoOperationStatus.Pending);
        }

        if (payload.Error is not null)
        {
            return new VeoOperation(VeoOperationStatus.Failed, Error: payload.Error.Message ?? "unknown Veo error");
        }

        var uri = payload.Response?.GenerateVideoResponse?.GeneratedSamples?
            .Select(sample => sample.Video?.Uri)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        return string.IsNullOrWhiteSpace(uri)
            ? new VeoOperation(VeoOperationStatus.Failed, Error: "Veo operation done with no video uri")
            : new VeoOperation(VeoOperationStatus.Succeeded, DownloadUri: uri);
    }

    public async Task<byte[]> DownloadAsync(string downloadUri, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadUri);

        // Absolute uri → BaseAddress is ignored; the x-goog-api-key default header still authenticates.
        using var response = await _http.GetAsync(downloadUri, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "video.download", cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        // Polly has already exhausted transient/429 retries; this is terminal.
        var bodyText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException(
            $"Veo {operation} failed: {(int)response.StatusCode} {response.StatusCode}. {Truncate(bodyText)}",
            inner: null,
            statusCode: response.StatusCode);
    }

    private static string Truncate(string value) =>
        value.Length <= ErrorBodyLogLimit ? value : value[..ErrorBodyLogLimit] + "…";

    [LoggerMessage(Level = LogLevel.Information, Message = "Veo: submitting {Model} at {AspectRatio} for {DurationSec}s (seeded={Seeded}).")]
    private partial void LogSubmitting(string model, string aspectRatio, int durationSec, bool seeded);

    // --- predictLongRunning DTOs (System.Text.Json web defaults => camelCase) ---

    private sealed record VeoGenerateRequest(
        [property: JsonPropertyName("instances")] IReadOnlyList<VeoInstance> Instances,
        [property: JsonPropertyName("parameters")] VeoParameters Parameters);

    private sealed record VeoInstance(
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("image")] VeoImage? Image);

    private sealed record VeoImage(
        [property: JsonPropertyName("inlineData")] VeoInlineData InlineData);

    private sealed record VeoInlineData(
        [property: JsonPropertyName("mimeType")] string MimeType,
        [property: JsonPropertyName("data")] string Data);

    private sealed record VeoParameters(
        [property: JsonPropertyName("aspectRatio")] string AspectRatio,
        // durationSeconds MUST serialize as a JSON NUMBER (e.g. 6), not a string ("6"): Veo returns
        // 400 "The value type for durationSeconds needs to be a number." The docs' curl examples quote
        // it, but that's shell interpolation — the wire type is a number (DL-058, live-confirmed).
        [property: JsonPropertyName("durationSeconds")] int DurationSeconds);

    // --- operation poll DTOs ---

    private sealed record VeoOperationResponse(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("done")] bool? Done,
        [property: JsonPropertyName("error")] VeoError? Error,
        [property: JsonPropertyName("response")] VeoResult? Response);

    private sealed record VeoError(
        [property: JsonPropertyName("code")] int? Code,
        [property: JsonPropertyName("message")] string? Message);

    private sealed record VeoResult(
        [property: JsonPropertyName("generateVideoResponse")] VeoGenerateVideoResponse? GenerateVideoResponse);

    private sealed record VeoGenerateVideoResponse(
        [property: JsonPropertyName("generatedSamples")] IReadOnlyList<VeoGeneratedSample>? GeneratedSamples);

    private sealed record VeoGeneratedSample(
        [property: JsonPropertyName("video")] VeoVideo? Video);

    private sealed record VeoVideo(
        [property: JsonPropertyName("uri")] string? Uri);
}
