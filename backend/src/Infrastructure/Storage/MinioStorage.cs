using Backend.Core.Storage;
using Backend.Infrastructure.Configuration.Options;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace Backend.Infrastructure.Storage;

/// <summary>
/// MinIO-backed <see cref="IStorageService"/> (DL-009). The endpoint is stored as
/// <c>host:port</c> only (the scaffold-hardening scheme rule); this class owns the
/// SSL decision, never a committed scheme. The target bucket is ensured on first
/// write. Object keys are brand-prefixed by the caller via <see cref="StorageKeys"/>,
/// so isolation in object storage is structural, mirroring the RLS prefix in Postgres.
/// </summary>
public sealed class MinioStorage : IStorageService
{
    private readonly IMinioClient _client;
    private readonly string _bucket;
    private int _bucketEnsured;

    public MinioStorage(IMinioClient client, IOptions<MinioOptions> options)
    {
        _client = client;
        _bucket = options.Value.Bucket;
    }

    public async Task<string> PutAsync(
        string key,
        byte[] content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        await EnsureBucketAsync(cancellationToken).ConfigureAwait(false);

        using var stream = new MemoryStream(content);
        await _client.PutObjectAsync(
            new PutObjectArgs()
                .WithBucket(_bucket)
                .WithObject(key)
                .WithStreamData(stream)
                .WithObjectSize(content.LongLength)
                .WithContentType(contentType),
            cancellationToken).ConfigureAwait(false);

        // Belt-and-suspenders. The Minio 6.0.3 client does NOT throw on a rejected write (e.g.
        // InvalidAccessKeyId / a permission denial): PutObjectAsync returns normally and the object
        // never lands. A silently-rejected write must surface as a fault, never a phantom success the
        // run carries to the human gate (DL-022/023). Confirm the object is actually readable back;
        // ExistsAsync itself throws when the backend rejects the follow-up stat.
        if (!await ExistsAsync(key, cancellationToken).ConfigureAwait(false))
        {
            throw new StorageException(
                $"Write to '{key}' did not persist (the storage backend accepted no object).");
        }

        return key;
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var stat = await _client.StatObjectAsync(
                new StatObjectArgs().WithBucket(_bucket).WithObject(key),
                cancellationToken).ConfigureAwait(false);

            // A rejected stat (auth/permission failure) is swallowed by the Minio 6.0.3 client into a
            // fabricated, empty result (Size=0, ETag="") instead of throwing — which would make this
            // return a false "exists". A genuinely-present object always carries a non-empty ETag, so
            // an empty one means the backend rejected the request: surface it, don't report a phantom.
            if (!IsRealObject(stat))
            {
                throw new StorageException(
                    $"Stat of '{key}' was rejected by the storage backend (no object metadata returned).");
            }

            return true;
        }
        catch (ObjectNotFoundException)
        {
            return false;
        }
        catch (BucketNotFoundException)
        {
            return false;
        }
    }

    public async Task<StorageObject?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            // Stat for the content type, then stream the object into memory (the asset is one small
            // image — no need for a streaming response). Returns null when the key is absent.
            var stat = await _client.StatObjectAsync(
                new StatObjectArgs().WithBucket(_bucket).WithObject(key),
                cancellationToken).ConfigureAwait(false);

            // A rejected read (auth/permission failure) is NOT thrown by the Minio 6.0.3 client: it
            // fabricates an empty stat (ETag="") and GetObjectAsync streams the backend's error body
            // (e.g. an InvalidAccessKeyId XML doc) into the callback. Serving that as the asset is the
            // DL-022/023 masking bug. A real object always has a non-empty ETag — anything else is a
            // rejection, surfaced so the media proxy returns a real error, not a 200 of an error doc.
            if (!IsRealObject(stat))
            {
                throw new StorageException(
                    $"Read of '{key}' was rejected by the storage backend (no object metadata returned).");
            }

            using var buffer = new MemoryStream();
            await _client.GetObjectAsync(
                new GetObjectArgs()
                    .WithBucket(_bucket)
                    .WithObject(key)
                    .WithCallbackStream(stream => stream.CopyTo(buffer)),
                cancellationToken).ConfigureAwait(false);

            // Defense in depth: the bytes received must match the object's real size; a mismatch means
            // the SDK streamed an error body instead of the object, never the asset itself.
            if (buffer.Length != stat.Size)
            {
                throw new StorageException(
                    $"Read of '{key}' returned {buffer.Length} bytes but the object is {stat.Size} bytes.");
            }

            // Default from the key's extension when the stored object reports no content type, so a
            // caller (e.g. the media proxy) never gets a null/empty type to choke on.
            var contentType = string.IsNullOrWhiteSpace(stat.ContentType)
                ? ContentTypeForKey(key)
                : stat.ContentType;
            return new StorageObject(buffer.ToArray(), contentType);
        }
        catch (ObjectNotFoundException)
        {
            return null;
        }
        catch (BucketNotFoundException)
        {
            return null;
        }
    }

    // The Minio 6.0.3 client swallows an auth/permission rejection (403) into a fabricated, empty
    // ObjectStat (Size=0, ETag="") rather than throwing. A genuinely-present object always carries a
    // non-empty ETag, so a missing ETag is the reliable "the backend rejected this request" signal —
    // it holds even when the object exists, because the 403 fires before the object is ever inspected.
    private static bool IsRealObject(ObjectStat stat) => !string.IsNullOrEmpty(stat.ETag);

    private static string ContentTypeForKey(string key) =>
        Path.GetExtension(key).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "application/octet-stream",
        };

    public async Task<IReadOnlyList<string>> ListAsync(
        string prefix,
        CancellationToken cancellationToken = default)
    {
        if (!await BucketExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var keys = new List<string>();
        var args = new ListObjectsArgs()
            .WithBucket(_bucket)
            .WithPrefix(prefix)
            .WithRecursive(true);

        await foreach (var item in _client.ListObjectsEnumAsync(args, cancellationToken)
                           .ConfigureAwait(false))
        {
            keys.Add(item.Key);
        }

        return keys;
    }

    private async Task<bool> BucketExistsAsync(CancellationToken cancellationToken) =>
        await _client.BucketExistsAsync(
            new BucketExistsArgs().WithBucket(_bucket), cancellationToken).ConfigureAwait(false);

    private async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _bucketEnsured, 1, 1) == 1)
        {
            return;
        }

        if (!await BucketExistsAsync(cancellationToken).ConfigureAwait(false))
        {
            await _client.MakeBucketAsync(
                new MakeBucketArgs().WithBucket(_bucket), cancellationToken).ConfigureAwait(false);
        }

        Interlocked.Exchange(ref _bucketEnsured, 1);
    }
}
