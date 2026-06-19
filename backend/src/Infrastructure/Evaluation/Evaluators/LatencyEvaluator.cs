using System.Globalization;
using Backend.Core.Evaluation;

namespace Backend.Infrastructure.Evaluation.Evaluators;

/// <summary>
/// §3 Latency observability (DL-022/023). Per-run wall-clock = (max span end − min span start) over the
/// durable trace spans (<c>TraceSpan.StartedAt/EndedAt</c>, persisted in <c>TraceRefs</c>).
///
/// <para><b>This is eval-environment wall-clock, NOT a production SLO.</b> Under the deterministic mock
/// clients the offline eval uses, the timings reflect the harness, not real model latency — so the metric
/// is named <c>eval_wallclock_ms</c> to signal that, and it is tracked-only: gating on it would measure the
/// harness, not the system. Production latency percentiles come from the real Langfuse generations.</para>
/// </summary>
public sealed class LatencyEvaluator : SystemOutputNumericEvaluator
{
    public const string MetricNameConst = "eval_wallclock_ms";

    protected override string MetricName => MetricNameConst;

    protected override (double Value, string Reason) Compute(SystemOutput output, EvalCase evalCase)
    {
        var spans = output.Trace.Spans;
        if (spans.Count == 0)
        {
            return (0.0, "no trace spans recorded");
        }

        var start = spans.Min(s => s.StartedAt);
        var end = spans.Max(s => s.EndedAt);
        var ms = Math.Max(0.0, (end - start).TotalMilliseconds);
        var reason = string.Create(
            CultureInfo.InvariantCulture,
            $"wall-clock across {spans.Count} span(s) = {ms:0.###} ms (eval environment, not an SLO)");
        return (ms, reason);
    }
}
