using System.Collections.Concurrent;
using Backend.Core.Storage;

namespace Backend.IntegrationTests.Support;

/// <summary>
/// In-memory <see cref="IStorageService"/> (the <c>LocalStorage</c> test double from
/// the boundary-interface spec). Lets the durability tests exercise the real media
/// step without standing up a MinIO container; key-by-key overwrite gives the same
/// idempotency semantics as MinIO.
/// </summary>
public sealed class InMemoryStorageService : IStorageService
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, byte[]> Objects => _objects;

    public Task<string> PutAsync(
        string key,
        byte[] content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        _objects[key] = content;
        return Task.FromResult(key);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_objects.ContainsKey(key));

    public Task<IReadOnlyList<string>> ListAsync(
        string prefix,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> matches = _objects.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(matches);
    }
}
