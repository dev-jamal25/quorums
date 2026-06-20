using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Core.Integrations;
using Backend.Core.Orchestration.Contracts;
using Backend.Infrastructure.Configuration.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Backend.Infrastructure.Integrations.Gemini;

/// <summary>
/// Real Gemini media backend behind <see cref="IMediaGenerationTool"/> (selected by
/// <c>Gemini:Mode=live</c>; mock stays for CI). Two paths share one seam (DL-058):
/// <list type="bullet">
///   <item><b>image</b> — renders a <see cref="MediaPromptBrief"/> into a Developer-API
///   <c>generateContent</c> request and maps the inline-data response to <see cref="MediaResult"/>.</item>
///   <item><b>video</b> — delegates to <see cref="VeoVideoGenerator"/> (the submit-or-resume async core);
///   for an image-seed run it first reuses the image path to make the Veo first frame, then animates it.</item>
/// </list>
/// On a non-success status or a missing part it <b>throws</b>; the Media node catches that into a
/// structured <c>ToolError</c> — image → fatal, video → caption-only degrade (DL-022/023) — never an
/// exception into the graph. The node owns the deterministic <c>assetId</c> + idempotent MinIO write;
/// this tool returns bytes only. <see cref="VeoVideoGenerator"/> is null unless <c>Veo:Mode=live</c>.
/// </summary>
public sealed partial class LiveGeminiMediaTool : IMediaGenerationTool
{
    private const string VideoModality = "video";
    private const int ErrorBodyLogLimit = 500;

    // Web defaults (camelCase) + omit null properties so the request's text part never emits
    // "inlineData": null (the part record is shared with the inline-data response shape).
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly GeminiOptions _options;
    private readonly VeoVideoGenerator? _veo;
    private readonly ILogger<LiveGeminiMediaTool> _logger;

    public LiveGeminiMediaTool(
        HttpClient http,
        IOptions<GeminiOptions> options,
        ILogger<LiveGeminiMediaTool> logger,
        VeoVideoGenerator? veo = null)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        _veo = veo;
    }

    public async Task<MediaResult> GenerateAsync(
        MediaGenerationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var brief = request.Brief;

        if (string.Equals(brief.Modality, VideoModality, StringComparison.OrdinalIgnoreCase))
        {
            return await GenerateVideoAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var (bytes, mimeType) = await GenerateImageBytesAsync(brief, cancellationToken).ConfigureAwait(false);
        return new MediaResult(bytes, mimeType);
    }

    private async Task<MediaResult> GenerateVideoAsync(
        MediaGenerationRequest request, CancellationToken cancellationToken)
    {
        if (_veo is null)
        {
            // Veo:Mode is not live → no video backend. Thrown; the Media node degrades the video run to
            // caption-only (DL-058 config-gating: an absent Veo never breaks the image path).
            throw new InvalidOperationException(
                "Veo video generation is not configured (Veo:Mode=live required).");
        }

        // Image-seed (default): make the Nano-Banana first frame via the image path (reuse, no duplication)
        // and hand it to Veo as the reference frame. Text-prompt: no seed, text-to-video.
        SeedImage? seed = null;
        if (request.Source == VideoSource.ImageSeed)
        {
            var (seedBytes, seedMime) = await GenerateImageBytesAsync(request.Brief, cancellationToken)
                .ConfigureAwait(false);
            seed = new SeedImage(seedBytes, seedMime);
        }

        return await _veo.GenerateAsync(request.Brief, seed, request.AssetId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<(byte[] Bytes, string MimeType)> GenerateImageBytesAsync(
        MediaPromptBrief brief, CancellationToken cancellationToken)
    {
        var request = new GeminiRequest(
            Contents: [new GeminiContent([new GeminiPart(MediaPromptRenderer.Render(brief), InlineData: null)])],
            GenerationConfig: new GeminiGenerationConfig(
                ResponseModalities: [ImageModalityToken],
                ImageConfig: new GeminiImageConfig(brief.AspectRatio)));

        var path = $"{_options.ApiVersion}/models/{_options.Model}:generateContent";
        LogGenerating(_options.Model, brief.AspectRatio);

        using var response = await _http
            .PostAsJsonAsync(path, request, _json, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // Polly has already exhausted transient/429 retries; this is a terminal failure.
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Gemini generateContent failed: {(int)response.StatusCode} {response.StatusCode}. {Truncate(body)}",
                inner: null,
                statusCode: response.StatusCode);
        }

        var payload = await response.Content
            .ReadFromJsonAsync<GeminiResponse>(_json, cancellationToken)
            .ConfigureAwait(false);

        var inline = payload?.Candidates?
            .SelectMany(candidate => candidate.Content?.Parts ?? [])
            .Select(part => part.InlineData)
            .FirstOrDefault(data => data is { Data.Length: > 0 });

        if (inline is null || string.IsNullOrEmpty(inline.Data))
        {
            throw new InvalidOperationException(
                "Gemini generateContent returned no inline image part (possible safety block or text-only response).");
        }

        var bytes = Convert.FromBase64String(inline.Data);
        var mimeType = string.IsNullOrWhiteSpace(inline.MimeType) ? "image/png" : inline.MimeType;
        LogGenerated(_options.Model, bytes.Length, mimeType);
        return (bytes, mimeType);
    }

    private static string Truncate(string value) =>
        value.Length <= ErrorBodyLogLimit ? value : value[..ErrorBodyLogLimit] + "…";

    // generateContent image responses use the "IMAGE" response modality token.
    private const string ImageModalityToken = "IMAGE";

    [LoggerMessage(Level = LogLevel.Debug, Message = "Gemini media: generating image via {Model} at {AspectRatio}.")]
    private partial void LogGenerating(string model, string aspectRatio);

    [LoggerMessage(Level = LogLevel.Information, Message = "Gemini media: {Model} returned {ByteCount} bytes ({MimeType}).")]
    private partial void LogGenerated(string model, int byteCount, string mimeType);

    // --- Developer-API generateContent DTOs (System.Text.Json web defaults => camelCase) ---

    private sealed record GeminiRequest(
        [property: JsonPropertyName("contents")] IReadOnlyList<GeminiContent> Contents,
        [property: JsonPropertyName("generationConfig")] GeminiGenerationConfig GenerationConfig);

    private sealed record GeminiContent(
        [property: JsonPropertyName("parts")] IReadOnlyList<GeminiPart> Parts);

    private sealed record GeminiPart(
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("inlineData")] GeminiInlineData? InlineData);

    private sealed record GeminiGenerationConfig(
        [property: JsonPropertyName("responseModalities")] IReadOnlyList<string> ResponseModalities,
        [property: JsonPropertyName("imageConfig")] GeminiImageConfig ImageConfig);

    private sealed record GeminiImageConfig(
        [property: JsonPropertyName("aspectRatio")] string AspectRatio);

    private sealed record GeminiResponse(
        [property: JsonPropertyName("candidates")] IReadOnlyList<GeminiCandidate>? Candidates);

    private sealed record GeminiCandidate(
        [property: JsonPropertyName("content")] GeminiContent? Content);

    private sealed record GeminiInlineData(
        [property: JsonPropertyName("mimeType")] string? MimeType,
        [property: JsonPropertyName("data")] string? Data);
}
