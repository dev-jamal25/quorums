using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Backend.Infrastructure.Evaluation;
using Backend.Infrastructure.Evaluation.Evaluators;
using Backend.IntegrationTests.Support;
using Microsoft.Extensions.AI.Evaluation;
using Xunit;

namespace Backend.IntegrationTests.Eval;

/// <summary>
/// The slice-5 hardening: <c>GeminiCallCount</c> counts EVERY <c>gemini.generate</c> span (ok + error), so
/// "zero Gemini calls on a budget breach" means zero ATTEMPTS, not zero successes (DL-023). On a real
/// breach the gate trips upstream and no span opens at all (count 0); but an errored attempt opens an
/// error span that must be counted — otherwise an errored call on a degraded run would read as zero and
/// the invariant would falsely pass. Deterministic, in-memory, no spend.
/// </summary>
[Trait("Category", "Eval")]
[Trait("Category", "EvalGate")]
public sealed class GeminiCallCountTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_errored_gemini_attempt_on_a_degraded_run_is_counted_and_reds_the_invariant()
    {
        // Hypothetical regression: the gate failed to trip and the Gemini call then errored → an error
        // span opened. Counting only ok-spans would read this as zero; counting all spans catches it.
        var span = new TraceSpan("g1", "media", "gemini.generate", "error", _t0, _t0.AddMilliseconds(10), "boom");
        var output = SystemOutputProjector.Project(DegradedState(span));

        Assert.Equal(1, output.GeminiCallCount);   // the ERROR span is now counted
        Assert.True(output.BudgetDegraded);
        Assert.False(await PassesBudgetInvariantAsync(output)); // degraded-yet-Gemini-attempted → red
    }

    [Fact]
    public async Task The_gate_trip_path_opens_no_gemini_span_so_the_count_stays_zero()
    {
        // The real breach path: the pre-Media gate degrades and records a BudgetDegraded span with NO
        // tool — no gemini.generate span opens, so the count is 0 and the invariant correctly passes.
        var span = new TraceSpan(
            "d1", "media", null, "degraded", _t0, _t0.AddMilliseconds(1), null, "{\"event\":\"BudgetDegraded\"}");
        var output = SystemOutputProjector.Project(DegradedState(span));

        Assert.Equal(0, output.GeminiCallCount);
        Assert.True(output.BudgetDegraded);
        Assert.True(await PassesBudgetInvariantAsync(output)); // proper caption-only degrade → pass
    }

    private static RunState DegradedState(params TraceSpan[] spans)
    {
        var brandId = Guid.NewGuid();
        var grounding = new Grounding(Grounded: false, ChunkIdsUsed: [], Confidence.Low);
        var caption = new Caption("Slow mornings start here", "A pour-over ritual.", ["#coffee"], grounding);
        var draft = new ContentItemDraft(caption, MediaRef: null, brandId, "degraded-caption-only");

        return TestGeneration.Seed(Guid.NewGuid(), brandId) with
        {
            Caption = caption,
            Draft = draft,
            Trace = new TraceRefs("t", spans.Select(s => s.SpanId).ToList(), [.. spans]),
        };
    }

    private static async Task<bool> PassesBudgetInvariantAsync(Backend.Core.Evaluation.SystemOutput output)
    {
        var (messages, response) = EvalTestData.Conversation();
        var context = new SystemOutputContext(output, EvalTestData.Case());
        var result = await new BudgetDegradationEvaluator().EvaluateAsync(messages, response, additionalContext: [context]);
        return result.Get<BooleanMetric>(BudgetDegradationEvaluator.MetricNameConst).Value == true;
    }
}
