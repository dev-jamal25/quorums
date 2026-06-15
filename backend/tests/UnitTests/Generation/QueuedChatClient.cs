using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Backend.UnitTests.Generation;

/// <summary>
/// A deterministic, network-free <see cref="IChatClient"/> double (the slice-2/3 CI-mock pattern):
/// it dequeues a canned <see cref="ChatResponse"/> per call and records the messages it received so
/// tests can assert the retry loop fed the error back. No real Claude call — CI never hits the wire.
/// </summary>
internal sealed class QueuedChatClient : IChatClient
{
    private static readonly JsonSerializerOptions _web = new(JsonSerializerDefaults.Web);

    private readonly Queue<Func<IReadOnlyList<ChatMessage>, ChatResponse>> _responders;

    public QueuedChatClient(params Func<IReadOnlyList<ChatMessage>, ChatResponse>[] responders) =>
        _responders = new Queue<Func<IReadOnlyList<ChatMessage>, ChatResponse>>(responders);

    /// <summary>The message lists received per call (the latest carries the accumulated retry feedback).</summary>
    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        Calls.Add(list);
        var responder = _responders.Dequeue();
        return Task.FromResult(responder(list));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("the forced-tool seam is non-streaming.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        // Nothing to dispose.
    }

    /// <summary>A response that calls <paramref name="toolName"/> with <paramref name="args"/> as the tool input.</summary>
    public static ChatResponse ToolCall(string toolName, object args)
    {
        var json = JsonSerializer.Serialize(args, _web);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(json, _web) ?? [];
        var content = new FunctionCallContent("call-1", toolName, arguments);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, [content]));
    }

    /// <summary>A prose-only response (no tool call) — proves a forcing failure path.</summary>
    public static ChatResponse TextOnly(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text));
}
