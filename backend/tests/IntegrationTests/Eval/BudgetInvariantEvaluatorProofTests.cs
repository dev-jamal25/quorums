using Backend.Core.Evaluation;
using Backend.Core.Orchestration;
using Backend.Infrastructure.Evaluation;
using Backend.Infrastructure.Evaluation.Evaluators;
using Microsoft.Extensions.AI.Evaluation;
using Xunit;

namespace Backend.IntegrationTests.Eval;

/// <summary>
/// The budget invariant — *never overspend, never crash, never fail silently* (DL-022/023) — as a complete
/// evaluator-level adversarial matrix. The <b>runtime</b> guarantee (a real media-budget breach makes ZERO
/// Gemini calls, proven by the absence of a <c>gemini.generate</c> ok-span on the durable trace) is already
/// proven end-to-end by <see cref="BudgetDegradationGenerationTests"/>; this slice does not duplicate that
/// spy. Here we prove the OBSERVABILITY guard reds on every way the invariant can be violated and passes
/// only on a clean degrade — so a regression that overspends, crashes, or drops the caption cannot slip
/// past the merge-blocking metric. Pure, deterministic, no LLM, no DB: zero API spend.
/// </summary>
[Trait("Category", "Eval")]
[Trait("Category", "EvalGate")]
public sealed class BudgetInvariantEvaluatorProofTests
{
    private static readonly ToolError _fatal = new("generation.media_failed", "boom", false);

    private static async Task<bool> PassedAsync(SystemOutput output)
    {
        var (messages, response) = EvalTestData.Conversation();
        var context = new SystemOutputContext(output, EvalTestData.Case());
        var result = await new BudgetDegradationEvaluator()
            .EvaluateAsync(messages, response, additionalContext: [context]);
        return result.Get<BooleanMetric>(BudgetDegradationEvaluator.MetricNameConst).Value == true;
    }

    // A correctly-degraded run: media unaffordable → caption-only, zero Gemini, no fatal.
    private static SystemOutput CleanDegrade()
    {
        var valid = EvalTestData.ValidOutput();
        return valid with
        {
            BudgetDegraded = true,
            GeminiCallCount = 0,
            Media = null,
            Draft = valid.Draft! with { MediaRef = null },
        };
    }

    [Fact]
    public async Task Passes_on_a_clean_caption_only_degrade()
    {
        Assert.True(await PassedAsync(CleanDegrade()));
    }

    [Fact]
    public async Task Passes_vacuously_when_the_run_did_not_degrade()
    {
        // A full, non-degraded run (media present, one Gemini call) — the invariant is not engaged.
        Assert.True(await PassedAsync(EvalTestData.ValidOutput()));
    }

    [Fact]
    public async Task Reds_when_a_degraded_run_still_made_a_gemini_call()
    {
        Assert.False(await PassedAsync(CleanDegrade() with { GeminiCallCount = 1 }));
    }

    [Fact]
    public async Task Reds_when_a_degraded_run_still_carries_a_media_asset()
    {
        // Overspend-leak: degraded yet a media asset is present (the gap the slice-1 tests did not cover).
        var valid = EvalTestData.ValidOutput();   // Media + Draft.MediaRef are non-null
        Assert.False(await PassedAsync(valid with { BudgetDegraded = true, GeminiCallCount = 0 }));
    }

    [Fact]
    public async Task Reds_when_a_degraded_run_failed_fatally()
    {
        // "never crash": degrade must produce a draft, not a fatal error.
        Assert.False(await PassedAsync(CleanDegrade() with { FatalError = _fatal }));
    }

    [Fact]
    public async Task Reds_when_a_degraded_run_dropped_the_caption()
    {
        // "never fail silently": Copywriting is unaffected by a media breach — the caption must survive.
        Assert.False(await PassedAsync(CleanDegrade() with { Caption = null }));
    }
}
