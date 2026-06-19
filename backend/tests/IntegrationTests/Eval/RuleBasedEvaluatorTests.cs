using Backend.Core.Evaluation;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Backend.Infrastructure.Evaluation;
using Backend.Infrastructure.Evaluation.Evaluators;
using Backend.IntegrationTests.Support;
using Microsoft.Extensions.AI.Evaluation;
using Xunit;

namespace Backend.IntegrationTests.Eval;

/// <summary>
/// The merge-blocking rule-based tool-call evaluators (evaluators.md §1) as custom
/// Microsoft.Extensions.AI.Evaluation <see cref="IEvaluator"/>s. Each is checked against a passing
/// <see cref="SystemOutput"/> and an adversarial one — a forced violation must red the metric. Pure,
/// deterministic, no LLM, no DB: zero API spend.
/// </summary>
[Trait("Category", "Eval")]
public sealed class RuleBasedEvaluatorTests
{
    private static async Task<bool> PassedAsync(IEvaluator evaluator, SystemOutput output, string metricName)
    {
        var (messages, response) = EvalTestData.Conversation();
        var context = new SystemOutputContext(output, EvalTestData.Case());
        var result = await evaluator.EvaluateAsync(messages, response, additionalContext: [context]);
        return result.Get<BooleanMetric>(metricName).Value == true;
    }

    [Fact]
    public async Task Schema_validity_passes_on_a_complete_run_and_fails_when_candidates_missing()
    {
        var evaluator = new SchemaValidityEvaluator();
        Assert.True(await PassedAsync(evaluator, EvalTestData.ValidOutput(), SchemaValidityEvaluator.MetricNameConst));

        var adversarial = EvalTestData.ValidOutput() with { Candidates = null };
        Assert.False(await PassedAsync(evaluator, adversarial, SchemaValidityEvaluator.MetricNameConst));
    }

    [Fact]
    public async Task Bounded_retry_passes_with_no_retries_and_fails_on_exhaustion_without_terminal_error()
    {
        var evaluator = new BoundedRetryEvaluator();
        Assert.True(await PassedAsync(evaluator, EvalTestData.ValidOutput(), BoundedRetryEvaluator.MetricNameConst));

        // Exhausted the bounded retries but never surfaced the terminal ToolError → fail.
        var adversarial = EvalTestData.ValidOutput() with
        {
            RetryCountsByNode = new Dictionary<string, int> { [SystemOutput.Nodes.ContentStrategist] = 2 },
            Errors = [],
        };
        Assert.False(await PassedAsync(evaluator, adversarial, BoundedRetryEvaluator.MetricNameConst));
    }

    [Fact]
    public async Task Bounded_retry_passes_on_a_correct_exhaustion_trajectory()
    {
        var evaluator = new BoundedRetryEvaluator();
        var correctExhaustion = EvalTestData.ValidOutput() with
        {
            RetryCountsByNode = new Dictionary<string, int> { [SystemOutput.Nodes.ContentStrategist] = 2 },
            Errors = [new ToolError("generation.schema_violation", "invalid after 2 retries", false)],
        };
        Assert.True(await PassedAsync(evaluator, correctExhaustion, BoundedRetryEvaluator.MetricNameConst));
    }

    [Fact]
    public async Task Platform_constraints_passes_in_range_and_fails_over_the_hashtag_limit()
    {
        var evaluator = new PlatformConstraintsEvaluator(TestGeneration.Constraints());
        Assert.True(await PassedAsync(evaluator, EvalTestData.ValidOutput(), PlatformConstraintsEvaluator.MetricNameConst));

        var output = EvalTestData.ValidOutput();
        var overLimit = output with
        {
            Caption = output.Caption! with { Hashtags = Enumerable.Range(0, 31).Select(i => $"#tag{i}").ToList() },
        };
        Assert.False(await PassedAsync(evaluator, overLimit, PlatformConstraintsEvaluator.MetricNameConst));
    }

    [Fact]
    public async Task Selection_index_passes_in_range_and_fails_out_of_range()
    {
        var evaluator = new SelectionIndexEvaluator();
        Assert.True(await PassedAsync(evaluator, EvalTestData.ValidOutput(), SelectionIndexEvaluator.MetricNameConst));

        var adversarial = EvalTestData.ValidOutput() with { ChosenIndex = 99 };
        Assert.False(await PassedAsync(evaluator, adversarial, SelectionIndexEvaluator.MetricNameConst));
    }

    [Fact]
    public async Task Objective_pillar_passes_for_a_playbook_pillar_and_fails_for_a_foreign_one()
    {
        var evaluator = new ObjectivePillarEvaluator();
        Assert.True(await PassedAsync(evaluator, EvalTestData.ValidOutput(), ObjectivePillarEvaluator.MetricNameConst));

        var output = EvalTestData.ValidOutput();
        var adversarial = output with { Strategy = output.Strategy! with { Pillar = "NotABrandPillar" } };
        Assert.False(await PassedAsync(evaluator, adversarial, ObjectivePillarEvaluator.MetricNameConst));
    }

    [Fact]
    public async Task Grounding_honesty_passes_for_honest_claims_and_fails_for_an_uninjected_claim()
    {
        var evaluator = new GroundingHonestyEvaluator();
        Assert.True(await PassedAsync(evaluator, EvalTestData.ValidOutput(), GroundingHonestyEvaluator.MetricNameConst));

        var output = EvalTestData.ValidOutput();
        var adversarial = output with
        {
            // raw claimed (from DL-054 provenance) cites a chunk id never injected for this node.
            ClaimedChunkIdsByNode = new Dictionary<string, IReadOnlyList<string>> { [SystemOutput.Nodes.Copywriting] = ["ghost-chunk"] },
            InjectedChunkIdsByNode = new Dictionary<string, IReadOnlyList<string>> { [SystemOutput.Nodes.Copywriting] = ["real-chunk"] },
        };
        Assert.False(await PassedAsync(evaluator, adversarial, GroundingHonestyEvaluator.MetricNameConst));
    }

    [Fact]
    public async Task Grounding_honesty_fails_when_a_node_claims_another_nodes_injected_id()
    {
        // Cross-node leak: Copywriting claims an id that was injected ONLY for the Creative Director.
        // The evaluator must look up the PER-NODE injected set, so the claim is dishonest for Copywriting.
        var evaluator = new GroundingHonestyEvaluator();
        var output = EvalTestData.ValidOutput();
        var adversarial = output with
        {
            ClaimedChunkIdsByNode = new Dictionary<string, IReadOnlyList<string>> { [SystemOutput.Nodes.Copywriting] = ["cd-only"] },
            InjectedChunkIdsByNode = new Dictionary<string, IReadOnlyList<string>>
            {
                [SystemOutput.Nodes.CreativeDirector] = ["cd-only"], // injected for node Y only
                [SystemOutput.Nodes.Copywriting] = ["cw-only"],      // node X's real injected set
            },
        };
        Assert.False(await PassedAsync(evaluator, adversarial, GroundingHonestyEvaluator.MetricNameConst));
    }

    [Fact]
    public async Task Grounding_honesty_reds_when_the_strategist_candidate_union_includes_an_uninjected_id()
    {
        // The Strategist's raw claimed is the UNION across its N=3 candidates (one dishonest candidate
        // lands an un-injected id in the union), audited against the Strategist's OWN injected set.
        var evaluator = new GroundingHonestyEvaluator();
        var adversarial = EvalTestData.ValidOutput() with
        {
            ClaimedChunkIdsByNode = new Dictionary<string, IReadOnlyList<string>> { [SystemOutput.Nodes.ContentStrategist] = ["A", "C"] },
            InjectedChunkIdsByNode = new Dictionary<string, IReadOnlyList<string>> { [SystemOutput.Nodes.ContentStrategist] = ["A", "B"] },
        };
        Assert.False(await PassedAsync(evaluator, adversarial, GroundingHonestyEvaluator.MetricNameConst));
    }

    [Fact]
    public async Task Bounded_retry_fails_when_a_node_exceeds_the_retry_bound()
    {
        // A node reporting more than the bounded 2 retries is a trajectory violation regardless of errors.
        var evaluator = new BoundedRetryEvaluator();
        var adversarial = EvalTestData.ValidOutput() with
        {
            RetryCountsByNode = new Dictionary<string, int> { [SystemOutput.Nodes.Copywriting] = 3 },
        };
        Assert.False(await PassedAsync(evaluator, adversarial, BoundedRetryEvaluator.MetricNameConst));
    }

    [Fact]
    public async Task Budget_degradation_passes_on_a_clean_caption_only_run_and_fails_if_gemini_was_called()
    {
        var evaluator = new BudgetDegradationEvaluator();

        var output = EvalTestData.ValidOutput();
        var degradedCorrectly = output with
        {
            BudgetDegraded = true,
            GeminiCallCount = 0,
            Media = null,
            Draft = output.Draft! with { MediaRef = null },
        };
        Assert.True(await PassedAsync(evaluator, degradedCorrectly, BudgetDegradationEvaluator.MetricNameConst));

        var degradedButSpent = output with { BudgetDegraded = true, GeminiCallCount = 1 };
        Assert.False(await PassedAsync(evaluator, degradedButSpent, BudgetDegradationEvaluator.MetricNameConst));
    }
}
