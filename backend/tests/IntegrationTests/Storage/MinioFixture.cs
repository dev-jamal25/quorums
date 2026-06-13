using Backend.Core.Storage;
using Backend.Infrastructure.Configuration.Options;
using Backend.Infrastructure.Storage;
using Microsoft.Extensions.Options;
using Minio;
using Testcontainers.Minio;
using Xunit;

namespace Backend.IntegrationTests.Storage;

/// <summary>
/// Spins up a disposable MinIO container and exposes a real <see cref="MinioStorage"/>
/// pointed at it, so the media-write seam is exercised against actual object storage
/// (not the in-memory double). Credentials are fixed on the builder and reused when
/// constructing the client.
/// </summary>
public sealed class MinioFixture : IAsyncLifetime
{
    private const string AccessKey = "minioadmin";
    private const string SecretKey = "minioadmin";
    private const string Bucket = "quorums-media-test";

    private readonly MinioContainer _container = new MinioBuilder()
        .WithUsername(AccessKey)
        .WithPassword(SecretKey)
        .Build();

    public IStorageService Storage { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var endpoint = $"{_container.Hostname}:{_container.GetMappedPublicPort(9000)}";
        var client = new MinioClient()
            .WithEndpoint(_container.Hostname, _container.GetMappedPublicPort(9000))
            .WithCredentials(AccessKey, SecretKey)
            .WithSSL(false)
            .Build();

        var options = Options.Create(new MinioOptions
        {
            Endpoint = endpoint,
            AccessKey = AccessKey,
            SecretKey = SecretKey,
            Bucket = Bucket,
        });

        Storage = new MinioStorage(client, options);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
