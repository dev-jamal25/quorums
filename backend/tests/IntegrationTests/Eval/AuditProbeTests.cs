using Backend.Core.Evaluation;
using Backend.Core.Orchestration;
using Backend.Infrastructure.Evaluation;
using Backend.Infrastructure.Orchestration.Maf.Nodes;
using Backend.Infrastructure.Tracing;
using Backend.IntegrationTests.Support;
using Xunit;

namespace Backend.IntegrationTests.Eval;

/// <summary>
/// Audit probe A1 — per-node injected-provenance attribution (DL-054), now resolved. The projector reads
/// each node's injected ids from that node's own durable provenance span, so attribution is intrinsic: a
/// node that retrieved nothing gets an EMPTY injected set, never the union across nodes (the defect this
/// probe caught when the test-support double unioned all retrieval calls).
/// </summary>
[Trait("Category", "Eval")]
public sealed class AuditProbeTests
{
    private static readonly string[] _strategyInjected = ["A"];
    private static readonly string[] _creativeInjected = ["B", "C"];

    [Fact]
    public async Task A1_projector_attributes_injected_ids_per_node_not_the_union()
    {
        var runId = Guid.NewGuid();
        var brandId = Guid.NewGuid();

        // Build a durable trace exactly as the executors do: a provenance span per node, each with its
        // OWN injected set. Copywriting "retrieved nothing" → empty injected.
        ITrace recorder = new LocalTraceRecorder();
        var trace = new TraceRefs(string.Empty, [], []);
        trace = await GroundingProvenance.RecordAsync(recorder, trace, runId, brandId, "strategy", [], ["A"], default);
        trace = await GroundingProvenance.RecordAsync(recorder, trace, runId, brandId, "creative", [], ["B", "C"], default);
        trace = await GroundingProvenance.RecordAsync(recorder, trace, runId, brandId, "copywriting", [], [], default);

        var state = TestGeneration.Seed(runId, brandId) with { Trace = trace };
        var output = SystemOutputProjector.Project(state);

        Assert.Equal(_strategyInjected, output.InjectedChunkIdsByNode[SystemOutput.Nodes.ContentStrategist]);
        Assert.Equal(_creativeInjected, output.InjectedChunkIdsByNode[SystemOutput.Nodes.CreativeDirector]);
        Assert.Empty(output.InjectedChunkIdsByNode[SystemOutput.Nodes.Copywriting]); // not the union {A,B,C}
    }
}
