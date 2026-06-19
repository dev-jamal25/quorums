using System.Text.Json;
using Microsoft.Extensions.AI.Evaluation;

namespace Backend.Infrastructure.Evaluation.Judges;

/// <summary>
/// Carries one calibration item plus the brand standards / grounding context the LLM judges cite, into the
/// Microsoft.Extensions.AI.Evaluation pipeline as an <see cref="EvaluationContext"/> (DL-057). The judges
/// read it via <c>additionalContext.OfType&lt;JudgeContext&gt;()</c>; the framework records the serialized
/// summary into the report.
/// </summary>
public sealed class JudgeContext : EvaluationContext
{
    public const string ContextName = "Quorums Judge Item";

    private static readonly JsonSerializerOptions _summaryJson =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public string Query { get; }

    /// <summary>The generated item text under evaluation.</summary>
    public string Output { get; }

    /// <summary>The brand's BrandPlaybook standards (Voice, Audience Persona, Mission, Visual Style).</summary>
    public string BrandStandards { get; }

    /// <summary>The factual grounding corpus (products, intel, posts) the output's claims must be supported by.</summary>
    public string GroundingContext { get; }

    public JudgeContext(string query, string output, string brandStandards, string groundingContext)
        : base(ContextName, Summarize(query, output))
    {
        Query = query;
        Output = output;
        BrandStandards = brandStandards;
        GroundingContext = groundingContext;
    }

    private static string Summarize(string query, string output) =>
        JsonSerializer.Serialize(new { query, output }, _summaryJson);
}
