namespace Backend.Core.Storage;

/// <summary>
/// Object storage seam (DL-009). Implementations are S3-portable; the default is
/// MinIO. Keys are brand-prefixed (<see cref="StorageKeys"/>) so brand isolation in
/// object storage mirrors the RLS story in Postgres — a brand can only ever read or
/// write under its own <c>brands/{brandId}/</c> prefix. Writes are keyed
/// deterministically so a retried Hangfire segment overwrites the same object rather
/// than creating a duplicate (DL-022).
/// </summary>
public interface IStorageService
{
    /// <summary>
    /// Uploads <paramref name="content"/> under <paramref name="key"/> and returns the
    /// stored key. Ensures the backing bucket exists. Overwrites an existing object at
    /// the same key (idempotent by key).
    /// </summary>
    Task<string> PutAsync(
        string key,
        byte[] content,
        string contentType,
        CancellationToken cancellationToken = default);

    /// <summary>True when an object exists at <paramref name="key"/>.</summary>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the object at <paramref name="key"/> (bytes + content type), or <c>null</c> when no object
    /// exists there. Used by the API media proxy to render a brand's asset; the key is resolved from the
    /// brand's own RLS-scoped run, so this read never crosses the structural <c>brands/{brandId}/</c>
    /// prefix.
    /// </summary>
    Task<StorageObject?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists object keys under <paramref name="prefix"/>. Used to prove prefix
    /// isolation: a brand-B prefix never surfaces a brand-A object.
    /// </summary>
    Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default);
}
