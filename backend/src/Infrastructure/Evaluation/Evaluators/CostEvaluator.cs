using System.Globalization;
using Backend.Core.Evaluation;
using Backend.Core.Generation.Cost;

namespace Backend.Infrastructure.Evaluation.Evaluators;

/// <summary>
/// §3 Cost observability (DL-022/023). Reads the run's <b>durable</b> usage — <c>Budget.TokensSpent</c>
/// (total, estimate-based) + the media image count (<c>GeminiCallCount</c>) — and converts it to a dollar
/// figure via <see cref="CostModel"/> with the config-bound <see cref="CostPrices"/>. Tracked, not
/// merge-blocking: no threshold (the cost-ceiling decision is frozen out of this slice). The figure is an
/// estimate (blended $/token; real per-tier actuals live in Langfuse — see <see cref="CostModel"/>).
/// </summary>
public sealed class CostEvaluator : SystemOutputNumericEvaluator
{
    public const string MetricNameConst = "Estimated Run Cost (USD)";

    private readonly CostPrices _prices;

    public CostEvaluator(CostPrices prices) => _prices = prices;

    protected override string MetricName => MetricNameConst;

    protected override (double Value, string Reason) Compute(SystemOutput output, EvalCase evalCase)
    {
        var cost = CostModel.RunCostUsd(output.Budget.TokensSpent, output.GeminiCallCount, _prices);
        var reason = string.Create(
            CultureInfo.InvariantCulture,
            $"{output.Budget.TokensSpent} tokens (estimate, blended rate) + {output.GeminiCallCount} image(s) = ${cost:0.######} (observability, not a billed figure)");
        return ((double)cost, reason);
    }
}
