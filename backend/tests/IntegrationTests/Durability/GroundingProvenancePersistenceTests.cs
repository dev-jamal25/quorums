using Backend.Core.Domain;
using Backend.Core.Evaluation;
using Backend.Core.Knowledge;
using Backend.Infrastructure.Evaluation;
using Backend.Infrastructure.Evaluation.Evaluators;
using Backend.IntegrationTests.Eval;
using Backend.IntegrationTests.Support;
using Microsoft.Extensions.AI.Evaluation;
using Xunit;

namespace Backend.IntegrationTests.Durability;

/// <summary>
/// DL-054 durability: per-node grounding provenance must survive PERSISTENCE — not just an in-process
/// round-trip. Runs a real ExecuteRun (which checkpoints <see cref="Backend.Core.Orchestration.RunState"/>
/// to Postgres), then RELOADS the run from Postgres (the slice-4 read path, the same one the trace +
/// status endpoints use) and audits grounding honesty from the <b>reloaded</b> trace — proving the span
/// <c>Detail</c> payloads (raw claimed + injected chunk ids) are stored with fidelity and readable later.
/// </summary>
[Trait("Category", "Trace")]
[Collection("Durability")]
public sealed class GroundingProvenancePersistenceTests
{
    private static readonly Guid _a = Guid.NewGuid();
    private static readonly Guid _b = Guid.NewGuid();

    private readonly DurabilityFixture _fixture;

    public GroundingProvenancePersistenceTests(DurabilityFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Provenance_survives_the_postgres_checkpoint_and_audits_from_the_reloaded_trace()
    {
        // Known injected set {A,B}; the model CLAIMS {A} (⊆ injected).
        var retrieval = new FakeRetrievalService(
        [
            new RetrievedChunk(_a, Guid.NewGuid(), "chunk A", DocType.BrandPlaybook, null, 0.9),
            new RetrievedChunk(_b, Guid.NewGuid(), "chunk B", DocType.Product, null, 0.9),
        ]);
        var (deps, chat) = TestGeneration.EvalDeps(retrieval: retrieval, groundingClaim: [_a.ToString()]);

        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA);
        var (execDb, execJob) = _fixture.CreateExecuteRunJob(_fixture.BrandA, deps);
        await using (execDb)
        {
            await execJob.ExecuteAsync(runId, _fixture.BrandA);
        }

        // RELOAD from Postgres — NOT the in-memory object the projector just used.
        var reloaded = await _fixture.ReadCheckpointStateAsync(runId, _fixture.BrandA);
        Assert.NotNull(reloaded);

        // The provenance spans persisted with their full Detail payloads, per node.
        var provenance = reloaded!.Trace.Spans.Where(s => s.Tool == "grounding.provenance").ToList();
        Assert.Contains(provenance, s => s.Node == "strategy"
            && s.Detail!.Contains(_a.ToString(), StringComparison.Ordinal)
            && s.Detail.Contains(_b.ToString(), StringComparison.Ordinal));
        Assert.Contains(provenance, s => s.Node == "creative");
        Assert.Contains(provenance, s => s.Node == "copywriting");

        // Project from the RELOADED trace → per-node claimed + injected, then audit honesty.
        var output = SystemOutputProjector.Project(reloaded, TestGeneration.OffStateRetries(chat));
        Assert.Equal(_a.ToString(), Assert.Single(output.ClaimedChunkIdsByNode[SystemOutput.Nodes.ContentStrategist]));
        Assert.Contains(_a.ToString(), output.InjectedChunkIdsByNode[SystemOutput.Nodes.ContentStrategist]);
        Assert.Contains(_b.ToString(), output.InjectedChunkIdsByNode[SystemOutput.Nodes.ContentStrategist]);

        var (messages, response) = EvalTestData.Conversation();
        var context = new SystemOutputContext(output, EvalTestData.Case());
        var result = await new GroundingHonestyEvaluator().EvaluateAsync(messages, response, additionalContext: [context]);
        Assert.Equal(true, result.Get<BooleanMetric>(GroundingHonestyEvaluator.MetricNameConst).Value);
    }
}
