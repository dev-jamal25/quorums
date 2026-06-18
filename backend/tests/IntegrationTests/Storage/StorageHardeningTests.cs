using Backend.Core.Storage;
using Backend.Infrastructure.Configuration.Options;
using Backend.Infrastructure.Orchestration.Maf;
using Backend.Infrastructure.Storage;
using Backend.IntegrationTests.Support;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using Testcontainers.Minio;
using Xunit;

namespace Backend.IntegrationTests.Storage;

/// <summary>
/// Adversarial proof for the storage-rejection hardening (DL-022/023). Reproduces the production
/// defect against a real MinIO container: the Minio 6.0.3 client does NOT throw on an auth-rejected
/// (403 <c>InvalidAccessKeyId</c>) object op — <c>PutObject</c> returns normally, <c>StatObject</c>
/// fabricates an empty result (so a naive <c>ExistsAsync</c> reports a phantom object), and
/// <c>GetObject</c> streams the backend's error XML as if it were the asset. A storage seam pointed at
/// the container with credentials the server rejects exercises exactly that path; the hardened
/// <see cref="MinioStorage"/> must turn each into a surfaced failure, and the run must fail loudly
/// rather than park a phantom draft at the human gate.
/// </summary>
[Trait("Category", "Storage")]
public sealed class StorageHardeningTests : IClassFixture<StorageHardeningTests.RejectedStorageFixture>
{
    private const string Key = "brands/hardening/assets/x.png";
    private static readonly byte[] _bytes = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

    private readonly RejectedStorageFixture _fixture;

    public StorageHardeningTests(RejectedStorageFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_rejected_write_surfaces_as_a_StorageException_not_a_phantom_success()
    {
        // Pre-hardening, PutObjectAsync returned normally on the 403 and the run carried a phantom
        // success to the gate. The belt-and-suspenders read-back must now surface it.
        await Assert.ThrowsAsync<StorageException>(
            () => _fixture.RejectedStorage.PutAsync(Key, _bytes, "image/png"));
    }

    [Fact]
    public async Task ExistsAsync_does_not_report_a_phantom_object_when_the_stat_is_rejected()
    {
        // Pre-hardening, the swallowed 403 fabricated an empty stat and ExistsAsync wrongly returned
        // true — defeating any "did the write land?" guard built on it.
        await Assert.ThrowsAsync<StorageException>(
            () => _fixture.RejectedStorage.ExistsAsync(Key));
    }

    [Fact]
    public async Task A_rejected_read_raises_instead_of_serving_the_backend_error_body()
    {
        // Seed a genuinely-present object with valid creds; the rejected read must STILL raise rather
        // than return the InvalidAccessKeyId XML the SDK streams as if it were a renderable image.
        await _fixture.ValidStorage.PutAsync(Key, _bytes, "image/png");

        await Assert.ThrowsAsync<StorageException>(
            () => _fixture.RejectedStorage.GetAsync(Key));
    }

    [Fact]
    public async Task Generation_with_a_rejected_storage_write_fails_the_run_not_a_phantom_draft()
    {
        var orchestrator = TestGeneration.Orchestrator(
            TestGeneration.Deps(storage: _fixture.RejectedStorage));

        var result = await orchestrator.RunGenerationAsync(
            TestGeneration.Seed(Guid.NewGuid(), Guid.NewGuid()));

        Assert.NotNull(result.FatalError);                                       // fails loudly (DL-022/023)
        Assert.Null(result.Media);                                               // no ref to a missing object
        Assert.Contains(result.Errors, e => e.Code == "media.generation_failed");
    }

    /// <summary>
    /// One MinIO container with a pre-created bucket; exposes a valid-credential storage (to seed real
    /// objects) and a storage whose credentials the server rejects (the 403 path under test).
    /// </summary>
    public sealed class RejectedStorageFixture : IAsyncLifetime
    {
        private const string RealUser = "minioadmin";
        private const string RealPass = "minioadmin";
        private const string Bucket = "quorums-media-reject-test";

        private readonly MinioContainer _container = new MinioBuilder()
            .WithUsername(RealUser).WithPassword(RealPass).Build();

        public IStorageService ValidStorage { get; private set; } = default!;
        public IStorageService RejectedStorage { get; private set; } = default!;

        public async Task InitializeAsync()
        {
            await _container.StartAsync();
            var host = _container.Hostname;
            var port = _container.GetMappedPublicPort(9000);

            // The bucket exists before any write, so the 403 is an object-op rejection (mirrors prod),
            // not a bucket-ensure failure.
            var good = Client(host, port, RealUser, RealPass);
            await good.MakeBucketAsync(new MakeBucketArgs().WithBucket(Bucket));
            ValidStorage = new MinioStorage(good, Opts(host, port, RealUser, RealPass));

            var bad = Client(host, port, "nonexistent-access-key", "nonexistent-secret-key");
            RejectedStorage = new MinioStorage(bad, Opts(host, port, "nonexistent-access-key", "nonexistent-secret-key"));
        }

        public Task DisposeAsync() => _container.DisposeAsync().AsTask();

        private static IMinioClient Client(string host, int port, string access, string secret) =>
            new MinioClient().WithEndpoint(host, port).WithCredentials(access, secret).WithSSL(false).Build();

        private static IOptions<MinioOptions> Opts(string host, int port, string access, string secret) =>
            Options.Create(new MinioOptions
            {
                Endpoint = $"{host}:{port}",
                AccessKey = access,
                SecretKey = secret,
                Bucket = Bucket,
            });
    }
}
