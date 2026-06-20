using Backend.Core.Domain;
using Backend.Core.Orchestration.Contracts;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Backend.IntegrationTests.Durability;

/// <summary>
/// DL-058 Decision 1 — per-run modality selection travels through Postgres, not the Hangfire payload
/// (DL-006). The adversarial proof: a Video run's modality is persisted on the <c>AgentRun</c> row,
/// <c>ExecuteRun</c> (whose payload is <c>runId</c> only) rebuilds <c>RunState</c> as Video, and a retry
/// for the same run is still Video — never reverting to image. Plus the no-regression image default.
/// </summary>
[Trait("Category", "Durability")]
[Collection("Durability")]
public sealed class RunModalitySelectionTests
{
    private readonly DurabilityFixture _fixture;

    public RunModalitySelectionTests(DurabilityFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Video_selection_persists_on_agent_run_and_rebuilds_into_runstate_across_retry()
    {
        // What POST /runs persists for { modality: Video, videoSource: ImageSeed }.
        var runId = await _fixture.SeedAgentRunAsync(
            _fixture.BrandA, modality: Modality.Video, videoSource: VideoSource.ImageSeed);

        // (1) Persisted on the AgentRun row, read under the brand's RLS scope.
        var (readDb, scope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (readDb)
        {
            await using var handle = await scope.BeginAsync();
            var run = await readDb.AgentRuns.AsNoTracking().FirstAsync(r => r.Id == runId);
            Assert.Equal(Modality.Video, run.Modality);
            Assert.Equal(VideoSource.ImageSeed, run.VideoSource);
            await handle.CompleteAsync();
        }

        // (2) ExecuteRun's payload is runId only — it reads modality from the AgentRun row (DL-006) and
        //     rebuilds RunState as Video on the 9:16 reel surface (DL-030).
        var (execDb, execJob) = _fixture.CreateExecuteRunJob(_fixture.BrandA);
        await using (execDb)
        {
            await execJob.ExecuteAsync(runId, _fixture.BrandA);
        }

        var afterGen = await _fixture.ReadCheckpointStateAsync(runId, _fixture.BrandA);
        Assert.Equal("video", afterGen!.Modality);
        Assert.Equal(VideoSource.ImageSeed, afterGen.VideoSource);
        Assert.Equal("instagram_reel", afterGen.TargetSurface);
        Assert.NotNull(afterGen.Media);
        Assert.Equal("video", afterGen.Media!.Modality);

        // (3) A retry of ExecuteRun for the SAME runId rebuilds from the same AgentRun row — still Video,
        //     never reverting to image (modality is not in the payload).
        var (exec2Db, exec2Job) = _fixture.CreateExecuteRunJob(_fixture.BrandA);
        await using (exec2Db)
        {
            await exec2Job.ExecuteAsync(runId, _fixture.BrandA);
        }

        var afterRetry = await _fixture.ReadCheckpointStateAsync(runId, _fixture.BrandA);
        Assert.Equal("video", afterRetry!.Modality);
        Assert.Equal(VideoSource.ImageSeed, afterRetry.VideoSource);
    }

    [Fact]
    public async Task No_modality_selection_is_an_image_run()
    {
        // What POST /runs persists with no body / no modality — exactly as before (no regression).
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA);

        var (execDb, execJob) = _fixture.CreateExecuteRunJob(_fixture.BrandA);
        await using (execDb)
        {
            await execJob.ExecuteAsync(runId, _fixture.BrandA);
        }

        var state = await _fixture.ReadCheckpointStateAsync(runId, _fixture.BrandA);
        Assert.Equal("image", state!.Modality);
        Assert.Equal("instagram_feed", state.TargetSurface);
        Assert.NotNull(state.Media);
        Assert.Equal("image", state.Media!.Modality);
    }
}
