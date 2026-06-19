using Microsoft.Extensions.AI;

namespace Backend.IntegrationTests.Support;

/// <summary>
/// Read-only recording double (Option A) decorating the deterministic generation <see cref="IChatClient"/>
/// to count calls per forced tool. <c>ForcedToolGenerator</c> calls the chat client once per attempt, so
/// <c>retries(node) = callCount(tool) - 1</c> — the per-node retry counts the bounded-retry evaluator
/// needs, which are otherwise internal to the generator. No production code changes.
/// </summary>
internal sealed class CountingChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly Dictionary<string, int> _callsByTool = new(StringComparer.Ordinal);

    public CountingChatClient(IChatClient inner) => _inner = inner;

    public IReadOnlyDictionary<string, int> CallCountsByTool => _callsByTool;

    /// <summary>Retries observed for a forced tool = attempts - 1 (one initial attempt is not a retry).</summary>
    public int RetriesForTool(string toolName) =>
        _callsByTool.TryGetValue(toolName, out var calls) ? Math.Max(0, calls - 1) : 0;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (ResolveToolName(options) is { Length: > 0 } tool)
        {
            _callsByTool[tool] = (_callsByTool.TryGetValue(tool, out var n) ? n : 0) + 1;
        }

        return _inner.GetResponseAsync(messages, options, cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        _inner.GetStreamingResponseAsync(messages, options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null) => _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();

    private static string? ResolveToolName(ChatOptions? options)
    {
        if (options?.ToolMode is RequiredChatToolMode { RequiredFunctionName: { } name })
        {
            return name;
        }

        return options?.Tools?.OfType<AITool>().FirstOrDefault()?.Name;
    }
}
