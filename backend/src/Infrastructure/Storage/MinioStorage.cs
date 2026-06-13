using Backend.Core.Storage;
using Backend.Infrastructure.Configuration.Options;
using Microsoft.Extensions.Options;
using Minio;
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

        return key;
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.StatObjectAsync(
                new StatObjectArgs().WithBucket(_bucket).WithObject(key),
                cancellationToken).ConfigureAwait(false);
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
