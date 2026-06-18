using Backend.Core.Evaluation;

namespace Backend.Infrastructure.Evaluation.Evaluators;

/// <summary>
/// §1.2 Bounded-retry trajectory — a malformed tool output triggers <b>exactly the bounded retries
/// (2) then a terminal <c>generation.schema_violation</c> <c>ToolError</c></b>, never an exception into
/// the graph and never an unbounded loop. Reads the per-node retry counts (mock-mode recording double)
/// + the <c>ToolError</c> list: a node that exhausted its retries MUST have surfaced the terminal
/// <c>ToolError</c>, and no node may exceed the bound. Happy paths (no retries) hold trivially.
/// </summary>
public sealed class BoundedRetryEvaluator : SystemOutputEvaluator
{
    public const string MetricNameConst = "Bounded Retry Trajectory";

    private const int MaxRetries = 2;

    protected override string MetricName => MetricNameConst;

    protected override Verdict Evaluate(SystemOutput output, EvalCase evalCase)
    {
        var maxObserved = output.RetryCountsByNode.Count == 0
            ? 0
            : output.RetryCountsByNode.Values.Max();

        if (maxObserved > MaxRetries)
        {
            return Verdict.Fail($"a node retried {maxObserved} times, exceeding the bound of {MaxRetries}");
        }

        var exhaustedNode = output.RetryCountsByNode
            .FirstOrDefault(kv => kv.Value >= MaxRetries);
        var exhausted = exhaustedNode.Value >= MaxRetries && exhaustedNode.Key is not null;

        var hasTerminal = output.Errors.Any(e => e.Code == GenerationErrorCodes.SchemaViolation)
            || output.FatalError?.Code == GenerationErrorCodes.SchemaViolation;

        if (exhausted && !hasTerminal)
        {
            return Verdict.Fail(
                $"node '{exhaustedNode.Key}' exhausted {MaxRetries} retries without a terminal generation.schema_violation ToolError");
        }

        return exhausted
            ? Verdict.Pass($"node '{exhaustedNode.Key}' retried {MaxRetries}x then surfaced a terminal ToolError")
            : Verdict.Pass("no node exceeded the bounded retries");
    }
}
