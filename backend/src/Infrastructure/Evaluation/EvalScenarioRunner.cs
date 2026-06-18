using System.Diagnostics;
using Backend.Core.Domain;
using Backend.Core.Evaluation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;

namespace Backend.Infrastructure.Evaluation;

/// <summary>Run-level provenance recorded on every <see cref="EvalRun"/> (DL-051, deck S16).</summary>
public sealed record EvalRunMetadata(
    string GitSha,
    string PromptVersion,
    string ModelName,
    string ModelVersion,
    double Temperature);

/// <summary>
/// The harness run path (slice 1) on Microsoft.Extensions.AI.Evaluation. For each dataset case it opens
/// a library <see cref="ScenarioRun"/> (which runs every configured <see cref="IEvaluator"/> and, on
/// dispose, writes the result to the disk store that <c>dotnet aieval</c> reads) and **dual-writes** the
/// same results to the RLS-scoped Postgres run store (DL-053). It **always persists, pass or fail** —
/// a red rule-based metric is still a recorded, queryable run (the adversarial proof).
/// </summary>
public sealed class EvalScenarioRunner
{
    private readonly EvalRunPersistence _persistence;

    public EvalScenarioRunner(EvalRunPersistence persistence) => _persistence = persistence;

    /// <summary>Maps every dataset case to one shared <see cref="SystemOutput"/> (a single-run eval).</summary>
    public static IReadOnlyDictionary<string, SystemOutput> SingleOutput(EvalDataset dataset, SystemOutput output)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        return dataset.Cases.ToDictionary(c => c.Id, _ => output);
    }

    public async Task<EvalRun> RunAsync(
        Guid brandId,
        EvalDataset dataset,
        ReportingConfiguration reportingConfiguration,
        IReadOnlyDictionary<string, SystemOutput> outputsByCaseId,
        EvalRunMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(reportingConfiguration);
        ArgumentNullException.ThrowIfNull(outputsByCaseId);
        ArgumentNullException.ThrowIfNull(metadata);

        var runId = Guid.NewGuid();
        var rows = new List<EvalResultRow>();

        foreach (var evalCase in dataset.Cases)
        {
            if (!outputsByCaseId.TryGetValue(evalCase.Id, out var output))
            {
                continue;
            }

            var (messages, response) = SynthesizeConversation(output);
            var context = new SystemOutputContext(output, evalCase);

            var stopwatch = Stopwatch.StartNew();
            await using (var scenarioRun = await reportingConfiguration
                .CreateScenarioRunAsync(
                    scenarioName: $"{dataset.Meta.Name}/{evalCase.Id}",
                    iterationName: metadata.GitSha,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false))
            {
                var result = await scenarioRun
                    .EvaluateAsync(messages, response, [context], cancellationToken)
                    .ConfigureAwait(false);
                stopwatch.Stop();

                foreach (var metric in result.Metrics.Values)
                {
                    rows.Add(new EvalResultRow
                    {
                        Id = Guid.NewGuid(),
                        RunId = runId,
                        BrandId = brandId,
                        CaseId = evalCase.Id,
                        EvaluatorName = metric.Name,
                        Score = MetricScore(metric),
                        Reasoning = metric.Reason,
                        CostUsd = null,
                        LatencyMs = stopwatch.ElapsedMilliseconds,
                        Metadata = BuildMetadata(metric),
                    });
                }
            }
            // scenarioRun disposed here → the disk store receives this case's result (for `dotnet aieval`).
        }

        var aggregates = rows
            .GroupBy(r => r.EvaluatorName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new MetricAggregate(group.Average(r => r.Score), group.Count()),
                StringComparer.Ordinal);

        var run = new EvalRun
        {
            Id = runId,
            BrandId = brandId,
            CreatedAt = DateTimeOffset.UtcNow,
            GitSha = metadata.GitSha,
            PromptVersion = metadata.PromptVersion,
            ModelName = metadata.ModelName,
            ModelVersion = metadata.ModelVersion,
            Temperature = metadata.Temperature,
            DatasetName = dataset.Meta.Name,
            DatasetVersion = dataset.Meta.Version,
            DatasetSize = dataset.Meta.Size,
            Aggregates = aggregates,
        };

        await _persistence.PersistAsync(run, rows, cancellationToken).ConfigureAwait(false);
        return run;
    }

    private static double MetricScore(EvaluationMetric metric) => metric switch
    {
        BooleanMetric boolean => boolean.Value == true ? 1.0 : 0.0,
        NumericMetric numeric => numeric.Value ?? 0.0,
        _ => 0.0,
    };

    private static Dictionary<string, object>? BuildMetadata(EvaluationMetric metric)
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

    // The evaluators read the real run from the SystemOutputContext; this minimal conversation is what
    // the library records into the report + uses as the (unused, no-LLM) response-cache key.
    private static (List<ChatMessage> Messages, ChatResponse Response) SynthesizeConversation(SystemOutput output)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, $"Generate on-brand Instagram content for brand {output.BrandId} ({output.TargetSurface})."),
        };

        var captionText = output.Caption is { } caption
            ? $"{caption.Hook}\n\n{caption.Body}"
            : "(no caption produced)";

        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, captionText));
        return (messages, response);
    }
}
