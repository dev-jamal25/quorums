using Microsoft.Extensions.AI;

namespace Backend.IntegrationTests.Support;

/// <summary>
/// A total-invocation counting decorator over an <see cref="IChatClient"/>. When the evaluation response
/// cache wraps this client, the cache sits OUTSIDE it — so a cache hit never reaches here and
/// <see cref="Calls"/> counts only cache misses, i.e. real (spending) judge calls. The cold calibration
/// run sees Calls &gt; 0; the cached replay sees Calls == 0 (the zero-spend CI proof). Forwards
/// <see cref="GetService"/> so the cache key metadata (model id) passes through unchanged.
/// </summary>
internal sealed class CallCountingChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private int _calls;

    public CallCountingChatClient(IChatClient inner) => _inner = inner;

    public int Calls => Volatile.Read(ref _calls);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return _inner.GetResponseAsync(messages, options, cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return _inner.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();
}
