using System.Text;
using Backend.Core.Common;
using Backend.Core.Orchestration;
using Backend.Core.Storage;
using Backend.IntegrationTests.Support;
using Xunit;

namespace Backend.IntegrationTests.Storage;

/// <summary>
/// DL-058 Slice-A: a real video run (image-seed) generates a deterministic mp4 that lands in actual MinIO
/// object storage at <c>brands/{brandId}/assets/{assetId}.mp4</c> with <c>Content-Type: video/mp4</c>, wired
/// into a single video draft (no carousel) — the generation half of the mock end-to-end proof. The write is
/// idempotent under a worker-crash re-run (DL-022). No live Veo/Gemini calls (deterministic mock).
/// </summary>
[Trait("Category", "Storage")]
public sealed class VideoStorageTests : IClassFixture<MinioFixture>
{
    private readonly MinioFixture _fixture;

    public VideoStorageTests(MinioFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Video_generation_writes_a_real_mp4_into_minio_and_assembles_a_video_draft()
    {
        var runId = Guid.NewGuid();
        var brandId = Guid.NewGuid();
        var orchestrator = TestGeneration.Orchestrator(TestGeneration.Deps(storage: _fixture.Storage));

        var state = await orchestrator.RunGenerationAsync(TestGeneration.VideoSeed(runId, brandId));

        Assert.Null(state.FatalError);
        Assert.Equal(GraphPhase.AwaitingApproval, state.Phase);

        // The single assembled asset is a 9:16 video ref at the deterministic .mp4 key.
        Assert.NotNull(state.Media);
        Assert.Equal("video", state.Media!.Modality);
        Assert.Equal("video/mp4", state.Media.MimeType);
        Assert.Equal(5, state.Media.DurationSec);
        var assetId = DeterministicGuid.From(runId, "asset");
        Assert.Equal(StorageKeys.ForAsset(brandId, assetId, "mp4"), state.Media.StorageKey);

        // Wired into a single video draft (one MediaRef — no carousel).
        Assert.NotNull(state.Draft);
        Assert.NotNull(state.Draft!.MediaRef);
        Assert.Equal("video/mp4", state.Draft.MediaRef!.MimeType);

        // A REAL mp4 object is in MinIO with the right content type + the ISO BMFF 'ftyp' signature.
        var stored = await _fixture.Storage.GetAsync(state.Media.StorageKey);
        Assert.NotNull(stored);
        Assert.Equal("video/mp4", stored!.ContentType);
        Assert.True(stored.Content.Length > 8);
        Assert.Equal("ftyp", Encoding.ASCII.GetString(stored.Content, 4, 4));

        // Idempotent: a crash re-run overwrites the one key (DL-022).
        await orchestrator.RunGenerationAsync(TestGeneration.VideoSeed(runId, brandId));
        Assert.Single(await _fixture.Storage.ListAsync(StorageKeys.AssetPrefix(brandId)));
    }
}
