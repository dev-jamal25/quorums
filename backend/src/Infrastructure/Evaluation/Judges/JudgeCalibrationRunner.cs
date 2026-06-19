using Backend.Core.Domain;
using Backend.Core.Evaluation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;

namespace Backend.Infrastructure.Evaluation.Judges;

/// <summary>One calibration item's judge-vs-human comparison on both κ-gated axes.</summary>
public sealed record JudgeItemAgreement(
    string Id, string? Tag, bool JudgeBrand, bool HumanBrand, bool JudgeGrounded, bool HumanGrounded)
{
    public bool BrandAgree => JudgeBrand == HumanBrand;

    public bool GroundedAgree => JudgeGrounded == HumanGrounded;
}

/// <summary>The calibration outcome: Cohen's κ per axis + the per-item agreement + the persisted run.</summary>
public sealed record JudgeCalibrationResult(
    double KappaBrand, double KappaGrounded, IReadOnlyList<JudgeItemAgreement> Items, EvalRun Run);

/// <summary>
/// Runs the brand + groundedness judges over the locked calibration set, binarizes each verdict, computes
/// Cohen's κ against the human labels per axis, and dual-writes the run RLS-scoped (DL-057). The judges run
/// through a caching <see cref="ReportingConfiguration"/> (built with the judge <c>ChatConfiguration</c>),
/// so the first run spends once and every replay reads the cache at zero spend.
/// </summary>
public sealed class JudgeCalibrationRunner
{
    private readonly EvalRunPersistence _persistence;

    public JudgeCalibrationRunner(EvalRunPersistence persistence) => _persistence = persistence;

    public async Task<JudgeCalibrationResult> RunAsync(
        Guid brandId,
        JudgeCalibrationSet set,
        ReportingConfiguration reportingConfiguration,
        EvalRunMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(reportingConfiguration);
        ArgumentNullException.ThrowIfNull(metadata);

        var brandStandards = CalibrationStandards.BrandStandards();
        var grounding = CalibrationStandards.GroundingContext();

        var runId = Guid.NewGuid();
        var rows = new List<EvalResultRow>();
        var items = new List<JudgeItemAgreement>();

        foreach (var calibrationCase in set.Cases)
        {
            var context = new JudgeContext(calibrationCase.Query, calibrationCase.Output, brandStandards, grounding);
            var messages = new List<ChatMessage> { new(ChatRole.User, calibrationCase.Query) };
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, calibrationCase.Output));

            await using var scenario = await reportingConfiguration
                .CreateScenarioRunAsync(calibrationCase.Id, iterationName: metadata.GitSha, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var result = await scenario.EvaluateAsync(messages, response, [context], cancellationToken).ConfigureAwait(false);

            var brandPass = PassOf(result, BrandConsistencyEvaluator.MetricNameConst);
            var groundedPass = PassOf(result, GroundednessJudgeEvaluator.MetricNameConst);

            items.Add(new JudgeItemAgreement(
                calibrationCase.Id, calibrationCase.Tag, brandPass, calibrationCase.BrandOn, groundedPass, calibrationCase.Grounded));

            rows.Add(Row(runId, brandId, calibrationCase.Id, BrandConsistencyEvaluator.MetricNameConst,
                brandPass, calibrationCase.BrandOn, ReasonOf(result, BrandConsistencyEvaluator.MetricNameConst)));
            rows.Add(Row(runId, brandId, calibrationCase.Id, GroundednessJudgeEvaluator.MetricNameConst,
                groundedPass, calibrationCase.Grounded, ReasonOf(result, GroundednessJudgeEvaluator.MetricNameConst)));
        }

        var n = items.Count;
        var kappaBrand = CohenKappa.Compute(items.Select(i => i.JudgeBrand).ToList(), items.Select(i => i.HumanBrand).ToList());
        var kappaGrounded = CohenKappa.Compute(items.Select(i => i.JudgeGrounded).ToList(), items.Select(i => i.HumanGrounded).ToList());

        var aggregates = new Dictionary<string, MetricAggregate>(StringComparer.Ordinal)
        {
            ["kappa.brand"] = new(kappaBrand, n),
            ["kappa.grounded"] = new(kappaGrounded, n),
            ["agreement.brand"] = new(items.Count(i => i.BrandAgree) / (double)n, n),
            ["agreement.grounded"] = new(items.Count(i => i.GroundedAgree) / (double)n, n),
        };

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
            DatasetName = "judge-calibration",
            DatasetVersion = set.Version,
            DatasetSize = set.N,
            Aggregates = aggregates,
        };

        await _persistence.PersistAsync(run, rows, cancellationToken).ConfigureAwait(false);
        return new JudgeCalibrationResult(kappaBrand, kappaGrounded, items, run);
    }

    private static bool PassOf(EvaluationResult result, string metricName) =>
        result.Get<BooleanMetric>(metricName).Value == true;

    private static string? ReasonOf(EvaluationResult result, string metricName) =>
        result.Get<BooleanMetric>(metricName).Reason;

    private static EvalResultRow Row(
        Guid runId, Guid brandId, string caseId, string evaluator, bool judgePass, bool humanLabel, string? reason) =>
        new()
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            BrandId = brandId,
            CaseId = caseId,
            EvaluatorName = evaluator,
            Score = judgePass ? 1.0 : 0.0,
            Reasoning = reason,
            CostUsd = null,
            LatencyMs = 0,
            Metadata = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["human_label"] = humanLabel,
                ["agree"] = judgePass == humanLabel,
            },
        };
}
