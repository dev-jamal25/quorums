using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Infrastructure.Configuration.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Backend.Infrastructure.Integrations.Gemini;

/// <summary>
/// A minimal Microsoft.Extensions.AI <see cref="IChatClient"/> over the Gemini Developer-API
/// <c>generateContent</c> (TEXT), for the Phase-9 LLM-judge tier (DL-057). There is no first-party Gemini
/// <c>IChatClient</c>, so this wraps the same typed-HttpClient pattern as the image tool: base address +
/// <c>x-goog-api-key</c> header + resilience are wired at registration. Maps chat messages → Gemini
/// contents (system → <c>systemInstruction</c>, assistant → <c>model</c> role) and returns the candidate
/// text. Exposes <see cref="ChatClientMetadata"/> so the evaluation response cache keys stably (model id),
/// enabling the cached, zero-spend CI replay. Non-streaming by use; streaming adapts the single response.
/// </summary>
public sealed class GeminiChatClient : IChatClient
{
    private static readonly JsonSerializerOptions _json =
        new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly HttpClient _http;
    private readonly string _model;
    private readonly string _apiVersion;
    private readonly ChatClientMetadata _metadata;

    public GeminiChatClient(HttpClient http, IOptions<GeminiOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        var value = options.Value;
        _model = string.IsNullOrWhiteSpace(value.JudgeModel) ? "gemini-2.5-flash" : value.JudgeModel;
        _apiVersion = value.ApiVersion;
        _metadata = new ChatClientMetadata("gemini", http.BaseAddress, _model);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var (system, contents) = Map(messages);
        var request = new GenRequest(
            Contents: contents,
            SystemInstruction: system is null ? null : new GenContent(null, [new GenPart(system)]),
            GenerationConfig: new GenConfig(options?.Temperature ?? 0f, options?.MaxOutputTokens));

        var path = $"{_apiVersion}/models/{_model}:generateContent";
        using var response = await _http.PostAsJsonAsync(path, request, _json, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Gemini judge generateContent failed: {(int)response.StatusCode} {response.StatusCode}. {body}",
                inner: null, statusCode: response.StatusCode);
        }

        var payload = await response.Content
            .ReadFromJsonAsync<GenResponse>(_json, cancellationToken).ConfigureAwait(false);
        var text = string.Concat((payload?.Candidates ?? [])
            .SelectMany(candidate => candidate.Content?.Parts ?? [])
            .Select(part => part.Text)
            .Where(t => !string.IsNullOrEmpty(t)));

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            ModelId = _model,
            FinishReason = ChatFinishReason.Stop,
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The judge evaluators call GetResponseAsync; adapt streaming by yielding the full text once.
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text) { ModelId = _model };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(ChatClientMetadata))
        {
            return _metadata;
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        // The typed HttpClient is owned by the DI container's HttpClientFactory.
    }

    private static (string? System, List<GenContent> Contents) Map(IEnumerable<ChatMessage> messages)
    {
        var system = new List<string>();
        var contents = new List<GenContent>();
        foreach (var message in messages)
        {
            var text = message.Text;
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            if (message.Role == ChatRole.System)
            {
                system.Add(text);
                continue;
            }

            var role = message.Role == ChatRole.Assistant ? "model" : "user";
            contents.Add(new GenContent(role, [new GenPart(text)]));
        }

        if (contents.Count == 0)
        {
            contents.Add(new GenContent("user", [new GenPart(" ")]));
        }

        return (system.Count == 0 ? null : string.Join("\n\n", system), contents);
    }

    private sealed record GenRequest(
        [property: JsonPropertyName("contents")] IReadOnlyList<GenContent> Contents,
        [property: JsonPropertyName("systemInstruction")] GenContent? SystemInstruction,
        [property: JsonPropertyName("generationConfig")] GenConfig GenerationConfig);

    private sealed record GenContent(
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("parts")] IReadOnlyList<GenPart> Parts);

    private sealed record GenPart([property: JsonPropertyName("text")] string Text);

    private sealed record GenConfig(
        [property: JsonPropertyName("temperature")] float Temperature,
        [property: JsonPropertyName("maxOutputTokens")] int? MaxOutputTokens);

    private sealed record GenResponse(
        [property: JsonPropertyName("candidates")] IReadOnlyList<GenCandidate>? Candidates);

    private sealed record GenCandidate(
        [property: JsonPropertyName("content")] GenContent? Content);
}
