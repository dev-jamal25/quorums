using Backend.Core.Orchestration;
using Backend.Core.Storage;
using Backend.IntegrationTests.Support;
using Xunit;

namespace Backend.IntegrationTests.Durability;

/// <summary>
/// Orchestrator-level (no DB) proof that the real MAF generation graph is idempotent: re-running the
/// generation segment writes a single asset — no duplicate asset under a worker-crash re-run (DL-022).
/// The publish segment now persists to Postgres (the coordinator owns a brand-scoped <c>PublishRecord</c>);
/// its idempotency is proven end-to-end in <c>PublishNodeTests</c> + the Slice-2 coordinator tests.
/// </summary>
[Trait("Category", "Durability")]
public sealed class MafOrchestratorIdempotencyTests
{
    [Fact]
    public async Task Generation_rerun_writes_a_single_asset()
    {
        var storage = new InMemoryStorageService();
        var orchestrator = TestGeneration.Orchestrator(TestGeneration.Deps(storage: storage));
        var runId = Guid.NewGuid();
        var brandId = Guid.NewGuid();
        var seed = TestGeneration.Seed(runId, brandId);

        // Generation twice = a worker crash before the checkpoint, then a Hangfire re-run.
        var g1 = await orchestrator.RunGenerationAsync(seed);
        var g2 = await orchestrator.RunGenerationAsync(seed);

        Assert.Equal(GraphPhase.AwaitingApproval, g1.Phase);
        Assert.NotNull(g1.Strategy);
        Assert.NotNull(g1.Creative);
        Assert.NotNull(g1.Caption);
        Assert.NotNull(g1.Media);
        Assert.NotNull(g1.Draft);
        Assert.Equal("pending", g1.Draft!.Status);
        Assert.NotNull(g2.Media);

        var assetKeys = await storage.ListAsync(StorageKeys.AssetPrefix(brandId));
        Assert.Single(assetKeys); // idempotent MinIO write under the MAF graph (DL-022)

        // One continuous trace with the four generation node/tool spans, ids in lockstep.
        Assert.False(string.IsNullOrEmpty(g1.Trace.TraceId));
        Assert.Contains(g1.Trace.Spans, s => s.Node == "strategy");
        Assert.Contains(g1.Trace.Spans, s => s.Node == "creative");
        Assert.Contains(g1.Trace.Spans, s => s.Node == "copywriting");
        Assert.Contains(g1.Trace.Spans, s => s.Node == "media" && s.Tool == "minio.put");
        Assert.Equal(g1.Trace.Spans.Count, g1.Trace.SpanIds.Count);
    }
}
