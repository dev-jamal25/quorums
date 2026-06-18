using Backend.Core.Orchestration;
using Backend.Infrastructure.Evaluation;
using Backend.Infrastructure.Evaluation.Evaluators;
using Backend.IntegrationTests.Support;
using Microsoft.Extensions.AI.Evaluation;
using Xunit;

namespace Backend.IntegrationTests.Eval;

/// <summary>
/// The budget-degradation invariant (evaluators.md §1) proven end-to-end on a real mock-mode generation:
/// a media budget below the per-image price forces the pre-Media gate to degrade. Asserts a caption-only
/// draft, zero Gemini calls, no fatal error — then confirms the <see cref="BudgetDegradationEvaluator"/>
/// passes on the projected run. Deterministic clients only → zero API spend.
/// </summary>
[Trait("Category", "Eval")]
public sealed class BudgetDegradationGenerationTests
{
    [Fact]
    public async Task Media_budget_breach_degrades_to_caption_only_with_zero_gemini_calls()
    {
        var (deps, retrieval, chat) = TestGeneration.EvalDeps();
        var orchestrator = TestGeneration.Orchestrator(deps);

        // MediaBudget (0.01) < GeminiPerImage (0.04) → unaffordable at the pre-Media gate (not fatal).
        var seed = TestGeneration.Seed(
            Guid.NewGuid(),
            Guid.NewGuid(),
            budget: new Budget(TokenBudget: 10_000, TokensSpent: 0, MediaBudget: 0.01m, MediaSpent: 0m));

        var state = await orchestrator.RunGenerationAsync(seed);

        var (injected, retries) = TestGeneration.OffState(retrieval, chat);
        var output = SystemOutputProjector.Project(state, injected, retries);

        Assert.Null(state.FatalError);
        Assert.True(output.BudgetDegraded);
        Assert.Equal(0, output.GeminiCallCount);
        Assert.NotNull(output.Caption);
        Assert.Null(output.Media);

        var evaluator = new BudgetDegradationEvaluator();
        var context = new SystemOutputContext(output, EvalTestData.Case());
        var (messages, response) = EvalTestData.Conversation();
        var result = await evaluator.EvaluateAsync(messages, response, additionalContext: [context]);

        Assert.Equal(true, result.Get<BooleanMetric>(BudgetDegradationEvaluator.MetricNameConst).Value);
    }
}
