using Backend.IntegrationTests.Durability;
using Xunit;

namespace Backend.IntegrationTests.Tracing;

/// <summary>
/// Verifies the trace surface that backs <c>GET /runs/{id}/trace</c>: spans exist for
/// every node and every tool call, the trace is one continuous id across the
/// ExecuteRun → ResumeRun seam, and trace data is brand-scoped (RLS), so one brand
/// cannot read another's trace. Reuses the durability fixture (real Postgres).
/// </summary>
[Trait("Category", "Trace")]
public sealed class TraceTests : IClassFixture<DurabilityFixture>
{
    private readonly DurabilityFixture _fixture;

    public TraceTests(DurabilityFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Completed_run_has_one_continuous_trace_with_node_and_tool_spans()
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA);

        var (execDb, execJob) = _fixture.CreateExecuteRunJob(_fixture.BrandA);
        await using (execDb) { await execJob.ExecuteAsync(runId, _fixture.BrandA); }

        await _fixture.ApproveRunAsync(runId, _fixture.BrandA);

        var (resumeDb, resumeJob) = _fixture.CreateResumeRunJob(_fixture.BrandA);
        await using (resumeDb) { await resumeJob.ExecuteAsync(runId, _fixture.BrandA); }

        var state = await _fixture.ReadCheckpointStateAsync(runId, _fixture.BrandA);
        Assert.NotNull(state);

        var trace = state!.Trace;
        Assert.False(string.IsNullOrEmpty(trace.TraceId));
        Assert.NotEmpty(trace.Spans);

        // Spans from both segments survive the pause/resume seam under one trace id.
        Assert.Contains(trace.Spans, s => s.Node == "strategy");
        Assert.Contains(trace.Spans, s => s.Node == "media" && s.Tool == "minio.put");
        Assert.Contains(trace.Spans, s => s.Node == "publishing" && s.Tool == "meta.publish");
        Assert.Equal(trace.Spans.Count, trace.SpanIds.Count);
        Assert.All(trace.Spans, s => Assert.False(string.IsNullOrEmpty(s.SpanId)));
        Assert.All(trace.Spans, s => Assert.Equal("ok", s.Status));
    }

    [Fact]
    [Trait("Category", "Isolation")]
    public async Task Brand_B_cannot_read_brand_A_trace()
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA);

        var (execDb, execJob) = _fixture.CreateExecuteRunJob(_fixture.BrandA);
        await using (execDb) { await execJob.ExecuteAsync(runId, _fixture.BrandA); }

        // The checkpoint carrying the trace is invisible under brand B's RLS scope.
        var underBrandB = await _fixture.ReadCheckpointStateAsync(runId, _fixture.BrandB);
        Assert.Null(underBrandB);
    }
}
