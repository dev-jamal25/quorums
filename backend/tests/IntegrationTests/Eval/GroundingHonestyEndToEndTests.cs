using Backend.Core.Domain;
using Backend.Core.Knowledge;
using Backend.Infrastructure.Evaluation;
using Backend.Infrastructure.Evaluation.Evaluators;
using Backend.IntegrationTests.Support;
using Microsoft.Extensions.AI.Evaluation;
using Xunit;

namespace Backend.IntegrationTests.Eval;

/// <summary>
/// The DL-054 end-to-end proof for grounding honesty — the chain the evaluator-unit tests could not
/// exercise. A mock generation that actually CLAIMS grounding (the deterministic client emits a non-empty
/// chunkIdsUsed) over a KNOWN injected set, driven through project → evaluate: the executors write
/// per-node provenance to the durable trace, the projector reads raw claimed + injected, and the
/// evaluator audits raw <c>claimed ⊆ injected</c>. Deterministic → zero API spend.
/// </summary>
[Trait("Category", "Eval")]
[Collection("EvalGeneration")]
public sealed class GroundingHonestyEndToEndTests
{
    private static readonly Guid _a = Guid.NewGuid();
    private static readonly Guid _b = Guid.NewGuid();
    private static readonly Guid _c = Guid.NewGuid();

    [Fact]
    public async Task Claimed_subset_of_injected_greens_end_to_end()
    {
        // injected {A,B}; claimed {A} ⊆ injected → honest.
        Assert.True(await GroundingHonestPassesAsync([_a.ToString()]));
    }

    [Fact]
    public async Task Claimed_includes_an_uninjected_id_reds_end_to_end()
    {
        // injected {A,B}; claimed {A,C} — C was never injected → faithfulness violation.
        Assert.False(await GroundingHonestPassesAsync([_a.ToString(), _c.ToString()]));
    }

    private static async Task<bool> GroundingHonestPassesAsync(IReadOnlyList<string> groundingClaim)
    {
        // FakeRetrievalService injects A (BrandPlaybook) + B (Product) into each grounding node's prompt.
        var retrieval = new FakeRetrievalService(
        [
            new RetrievedChunk(_a, Guid.NewGuid(), "chunk A", DocType.BrandPlaybook, null, 0.9),
            new RetrievedChunk(_b, Guid.NewGuid(), "chunk B", DocType.Product, null, 0.9),
        ]);

        var (deps, chat) = TestGeneration.EvalDeps(retrieval: retrieval, groundingClaim: groundingClaim);
        var orchestrator = TestGeneration.Orchestrator(deps);
        var state = await orchestrator.RunGenerationAsync(TestGeneration.Seed(Guid.NewGuid(), Guid.NewGuid()));

        var output = SystemOutputProjector.Project(state, TestGeneration.OffStateRetries(chat));

        var (messages, response) = EvalTestData.Conversation();
        var context = new SystemOutputContext(output, EvalTestData.Case());
        var result = await new GroundingHonestyEvaluator().EvaluateAsync(messages, response, additionalContext: [context]);
        return result.Get<BooleanMetric>(GroundingHonestyEvaluator.MetricNameConst).Value == true;
    }
}
