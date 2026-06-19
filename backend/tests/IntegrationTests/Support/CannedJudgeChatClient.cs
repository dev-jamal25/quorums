using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Backend.IntegrationTests.Support;

/// <summary>
/// A deterministic fake judge <see cref="IChatClient"/>: returns a canned verdict computed from the last
/// user prompt, so the LLM-judge parsing/binarization and the calibration harness (binarize → κ → persist)
/// can be proven with ZERO spend, before any real Gemini call. Counts invocations so a later test can show
/// a cached replay makes none.
/// </summary>
public sealed class CannedJudgeChatClient : IChatClient
{
    private readonly Func<string, string> _respond;
    private readonly ChatClientMetadata _metadata = new("canned-judge", providerUri: null, defaultModelId: "canned");
    private int _calls;

    public CannedJudgeChatClient(Func<string, string> respond) => _respond = respond;

    public int Calls => Volatile.Read(ref _calls);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        var userText = messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text).LastOrDefault() ?? string.Empty;
        var text = _respond(userText);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { ModelId = "canned" });
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? _metadata : null;

    public void Dispose()
    {
    }
}
