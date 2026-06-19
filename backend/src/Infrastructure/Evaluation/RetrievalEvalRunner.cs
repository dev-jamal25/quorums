using System.Diagnostics;
using System.Text.Json;
using Backend.Core.Domain;
using Backend.Core.Evaluation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;

namespace Backend.Infrastructure.Evaluation;

/// <summary>One golden case's retrieved ranking and the rank-metric values it scored.</summary>
public sealed record RetrievalEvalCaseResult(
    string CaseId,
    IReadOnlyList<Guid> RankedChunkIds,
    IReadOnlyDictionary<string, double> Metrics);

/// <summary>The persisted <see cref="EvalRun"/> for one retrieval-config arm plus its per-case results.</summary>
public sealed record RetrievalEvalRunResult(EvalRun Run, IReadOnlyList<RetrievalEvalCaseResult> Cases);

/// <summary>
/// The reference-based retrieval harness path (DL-048/025) on Microsoft.Extensions.AI.Evaluation. Given the
/// ranked chunk-id list each golden query already produced (the caller drives the real RLS-scoped retrieval
/// pipeline), it scores every case with the rank-metric <see cref="IEvaluator"/>s via a library
/// <see cref="ScenarioRun"/> (disk store for <c>dotnet aieval</c>) and **dual-writes** the same results to
/// the RLS-scoped Postgres run store — exactly like <see cref="EvalScenarioRunner"/>. One call = one
/// config arm; the arm's config descriptor rides <see cref="EvalRunMetadata.PromptVersion"/> (the "what
/// produced this" provenance for a retrieval eval, reusing the existing schema — no migration), so the
/// paired stage ablation persists each arm distinctly.
/// </summary>
public sealed class RetrievalEvalRunner
{
    private readonly EvalRunPersistence _persistence;

    public RetrievalEvalRunner(EvalRunPersistence persistence) => _persistence = persistence;

    public async Task<RetrievalEvalRunResult> RunAsync(
        Guid brandId,
        EvalDataset dataset,
        ReportingConfiguration reportingConfiguration,
        IReadOnlyDictionary<string, IReadOnlyList<Guid>> rankedByCaseId,
        EvalRunMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(reportingConfiguration);
        ArgumentNullException.ThrowIfNull(rankedByCaseId);
        ArgumentNullException.ThrowIfNull(metadata);

        var runId = Guid.NewGuid();
        var rows = new List<EvalResultRow>();
        var caseResults = new List<RetrievalEvalCaseResult>();

        foreach (var evalCase in dataset.Cases)
        {
            if (!rankedByCaseId.TryGetValue(evalCase.Id, out var ranked))
            {
                continue;
            }

            var query = QueryOf(evalCase);
            var relevant = RelevantChunkIds(evalCase);
            var context = new RetrievalEvalContext(evalCase.Id, query, ranked, relevant);
            var (messages, response) = SynthesizeConversation(query, ranked);

            var metricsForCase = new Dictionary<string, double>(StringComparer.Ordinal);
            var stopwatch = Stopwatch.StartNew();
            await using (var scenarioRun = await reportingConfiguration
                .CreateScenarioRunAsync(
                    scenarioName: evalCase.Id,
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
                    var score = EvalMetricMapping.Score(metric);
                    metricsForCase[metric.Name] = score;
                    rows.Add(new EvalResultRow
                    {
                        Id = Guid.NewGuid(),
                        RunId = runId,
                        BrandId = brandId,
                        CaseId = evalCase.Id,
                        EvaluatorName = metric.Name,
                        Score = score,
                        Reasoning = metric.Reason,
                        CostUsd = null,
                        LatencyMs = stopwatch.ElapsedMilliseconds,
                        Metadata = EvalMetricMapping.BuildMetadata(metric),
                    });
                }
            }

            caseResults.Add(new RetrievalEvalCaseResult(evalCase.Id, ranked, metricsForCase));
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
        return new RetrievalEvalRunResult(run, caseResults);
    }

    private static string QueryOf(EvalCase evalCase) =>
        evalCase.Input.TryGetProperty("query", out var q) && q.GetString() is { } query
            ? query
            : throw new InvalidOperationException($"golden case '{evalCase.Id}' has no string 'query' input");

    private static List<Guid> RelevantChunkIds(EvalCase evalCase) =>
        evalCase.Expected.TryGetProperty("relevant_chunk_ids", out var ids) && ids.ValueKind == JsonValueKind.Array
            ? ids.EnumerateArray().Select(e => Guid.Parse(e.GetString()!)).ToList()
            : throw new InvalidOperationException($"golden case '{evalCase.Id}' has no 'relevant_chunk_ids' array");

    // The evaluators read the ranking from the RetrievalEvalContext; this minimal conversation is what the
    // library records into the report (and the unused, no-LLM response-cache key).
    private static (List<ChatMessage> Messages, ChatResponse Response) SynthesizeConversation(
        string query, IReadOnlyList<Guid> ranked)
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, query) };
        var response = new ChatResponse(new ChatMessage(
            ChatRole.Assistant, string.Join(", ", ranked.Select(id => id.ToString()))));
        return (messages, response);
    }
}
