using System.Text.Json;
using Microsoft.Extensions.AI.Evaluation;

namespace Backend.Infrastructure.Evaluation;

/// <summary>
/// Carries a single golden query's ranked retrieval result (chunk ids in rank order) and its golden
/// relevant set <c>R_q</c> into the Microsoft.Extensions.AI.Evaluation pipeline as an
/// <see cref="EvaluationContext"/> — the same pattern as <see cref="SystemOutputContext"/>. The custom
/// rank-metric evaluators read the strongly-typed ids via
/// <c>additionalContext.OfType&lt;RetrievalEvalContext&gt;()</c>; the serialized summary in
/// <see cref="EvaluationContext.Contents"/> is what the library records into the on-disk report.
/// </summary>
public sealed class RetrievalEvalContext : EvaluationContext
{
    public const string ContextName = "Quorums Retrieval Result";

    private static readonly JsonSerializerOptions _summaryJson =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public string CaseId { get; }

    public string Query { get; }

    /// <summary>The retrieved chunk ids in rank order (top-k = the final cut).</summary>
    public IReadOnlyList<Guid> RankedChunkIds { get; }

    /// <summary>The hand-labelled golden relevant set <c>R_q</c>.</summary>
    public IReadOnlyCollection<Guid> RelevantChunkIds { get; }

    public RetrievalEvalContext(
        string caseId,
        string query,
        IReadOnlyList<Guid> rankedChunkIds,
        IReadOnlyCollection<Guid> relevantChunkIds)
        : base(ContextName, Summarize(caseId, query, rankedChunkIds, relevantChunkIds))
    {
        CaseId = caseId;
        Query = query;
        RankedChunkIds = rankedChunkIds;
        RelevantChunkIds = relevantChunkIds;
    }

    private static string Summarize(
        string caseId, string query, IReadOnlyList<Guid> ranked, IReadOnlyCollection<Guid> relevant) =>
        JsonSerializer.Serialize(
            new
            {
                caseId,
                query,
                ranked = ranked.Select(id => id.ToString()).ToArray(),
                relevant = relevant.Select(id => id.ToString()).ToArray(),
            },
            _summaryJson);
}
