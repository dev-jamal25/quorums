using Backend.Core.Knowledge;

namespace Backend.IntegrationTests.Support;

/// <summary>
/// A spy <see cref="IRerankProvider"/> that counts cross-encoder invocations and delegates to a real
/// inner provider. Used to prove the S2 rerank stage is genuinely **skipped** when off (zero calls ⇒ no
/// hop, no reorder), and that it IS invoked when explicitly enabled (so the zero-count is the config gate,
/// not a silently wired-out provider).
/// </summary>
public sealed class CountingRerankProvider : IRerankProvider
{
    private readonly IRerankProvider _inner;
    private int _calls;

    public CountingRerankProvider(IRerankProvider inner) => _inner = inner;

    /// <summary>The number of times the cross-encoder was actually invoked.</summary>
    public int Calls => Volatile.Read(ref _calls);

    public Task<IReadOnlyList<RerankScore>> RerankAsync(
        string query, IReadOnlyList<string> documents, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return _inner.RerankAsync(query, documents, cancellationToken);
    }
}
