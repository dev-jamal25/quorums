using Backend.Core.Domain;
using Backend.Core.Orchestration;
using Backend.Core.Storage;
using Backend.Infrastructure.Orchestration.Maf;
using Backend.IntegrationTests.Support;
using Xunit;

namespace Backend.IntegrationTests.Durability;

/// <summary>
/// Orchestrator-level (no DB) proof that the real MAF graph keeps the c1/c2 side effects
/// idempotent: re-running the generation segment writes a single asset, and re-running the
/// publish segment re-uses the same external reference — no duplicate asset, no double post.
/// </summary>
[Trait("Category", "Durability")]
public sealed class MafOrchestratorIdempotencyTests
{
    [Fact]
    public async Task Generation_rerun_writes_single_asset_then_publish_rerun_keeps_one_ref()
    {
        var storage = new InMemoryStorageService();
        var meta = new RecordingMetaIntegration();
        var orchestrator = new MafOrchestrator(TestGeneration.Deps(storage: storage), meta);
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

        // Publish twice = a publish-segment crash, then a retry.
        var p1 = await orchestrator.RunPublishAsync(g1);
        var p2 = await orchestrator.RunPublishAsync(g1);

        Assert.Equal(GraphPhase.Done, p1.Phase);
        Assert.StartsWith("mock://meta/", p1.Publish!.ExternalRef!);
        Assert.Equal(PublishStatus.Published, p1.Publish.Status);
        Assert.Equal(p1.Publish.ExternalRef, p2.Publish!.ExternalRef); // deterministic ref (DL-022)
        Assert.Single(meta.PublishedRefs.Distinct());                  // no second distinct post
    }
}
