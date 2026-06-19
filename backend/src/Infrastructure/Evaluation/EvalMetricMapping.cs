using Microsoft.Extensions.AI.Evaluation;

namespace Backend.Infrastructure.Evaluation;

/// <summary>
/// Maps a library <see cref="EvaluationMetric"/> to the persisted <c>eval_result</c> shape — the single
/// place boolean/numeric metrics become a 0..1 score and the interpretation/diagnostics become the jsonb
/// metadata. Shared by the generation-eval (<see cref="EvalScenarioRunner"/>) and retrieval-eval
/// (<see cref="RetrievalEvalRunner"/>) harness paths so both record identically.
/// </summary>
internal static class EvalMetricMapping
{
    public static double Score(EvaluationMetric metric) => metric switch
    {
        BooleanMetric boolean => boolean.Value == true ? 1.0 : 0.0,
        NumericMetric numeric => numeric.Value ?? 0.0,
        _ => 0.0,
    };

    public static Dictionary<string, object>? BuildMetadata(EvaluationMetric metric)
    {
        var metadata = new Dictionary<string, object>(StringComparer.Ordinal);

        if (metric.Interpretation is { } interpretation)
        {
            metadata["failed"] = interpretation.Failed;
            metadata["rating"] = interpretation.Rating.ToString();
        }

        if (metric.Diagnostics is { Count: > 0 } diagnostics)
        {
            metadata["diagnostics"] = diagnostics
                .Select(diagnostic => $"{diagnostic.Severity}: {diagnostic.Message}")
                .ToArray();
        }

        return metadata.Count > 0 ? metadata : null;
    }
}
