using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;

namespace Backend.Infrastructure.Evaluation;

/// <summary>
/// Builds the library's <see cref="ReportingConfiguration"/> backed by the **disk** result store +
/// response cache (DL-053) — the store the <c>dotnet aieval</c> CLI reads to generate the HTML/comparison
/// report. The RLS-scoped Postgres run store is written separately (dual-write) by
/// <see cref="EvalScenarioRunner"/>. The rule-based evaluators make no LLM call, so
/// <c>chatConfiguration</c> is null here; response caching is enabled so the plumbing is ready for the
/// judge tier (slice 6).
/// </summary>
public static class EvalReportingFactory
{
    /// <summary>Env var pointing at the disk store root (so CI + `dotnet aieval` agree on a path).</summary>
    public const string StorageRootEnvVar = "EVAL_REPORT_STORE";

    public static string ResolveStorageRoot() =>
        Environment.GetEnvironmentVariable(StorageRootEnvVar) is { Length: > 0 } configured
            ? configured
            : Path.Combine(Path.GetTempPath(), "quorums-eval-store");

    public static ReportingConfiguration CreateDiskReporting(
        IEnumerable<IEvaluator> evaluators,
        string? storageRootPath = null,
        string executionName = "local",
        IEnumerable<string>? tags = null) =>
        DiskBasedReportingConfiguration.Create(
            storageRootPath: storageRootPath ?? ResolveStorageRoot(),
            evaluators: evaluators,
            chatConfiguration: null,
            enableResponseCaching: true,
            executionName: executionName,
            tags: tags);

    /// <summary>
    /// Reporting for the LLM-judge tier (DL-057): carries the judge <paramref name="chatConfiguration"/> and
    /// enables response caching so the one-time calibration spend is replayed at zero cost (the CI path).
    /// The cache TTL is set effectively unbounded so a committed cache never silently expires CI.
    /// </summary>
    public static ReportingConfiguration CreateJudgeReporting(
        IEnumerable<IEvaluator> evaluators,
        ChatConfiguration chatConfiguration,
        string storageRootPath,
        string executionName = "judge") =>
        DiskBasedReportingConfiguration.Create(
            storageRootPath: storageRootPath,
            evaluators: evaluators,
            chatConfiguration: chatConfiguration,
            enableResponseCaching: true,
            timeToLiveForCacheEntries: TimeSpan.FromDays(36_500),
            executionName: executionName);
}
