using System.ComponentModel.DataAnnotations;
using Backend.Core.Generation.Cost;

namespace Backend.Infrastructure.Configuration.Options;

/// <summary>
/// The cost-model prices (DL-029), config-bound and seeded with current live values at
/// build/config time — never hardcoded in agent code nor recalled from memory. Token prices are
/// per million tokens; the Gemini price is per image. A positive value is required so an unseeded
/// price fails validation at startup rather than silently costing $0. Maps to the pure-domain
/// <see cref="CostPrices"/> the budget functions consume.
/// </summary>
public sealed class CostPricesOptions
{
    public const string SectionName = "CostPrices";

    [Range(0.0000001, 1_000_000.0)]
    public decimal SonnetInputPerMTok { get; init; }

    [Range(0.0000001, 1_000_000.0)]
    public decimal SonnetOutputPerMTok { get; init; }

    [Range(0.0000001, 1_000_000.0)]
    public decimal HaikuInputPerMTok { get; init; }

    [Range(0.0000001, 1_000_000.0)]
    public decimal HaikuOutputPerMTok { get; init; }

    [Range(0.0000001, 1_000_000.0)]
    public decimal GeminiPerImage { get; init; }

    /// <summary>
    /// The global per-run dollar ceiling the Media gate enforces (DL-029). A grossly-exceeded
    /// fork-time snapshot fails the run. Config-bound (seeded near
    /// <c>BudgetEvaluation.WorstCaseCeilingUsd</c>); tests inject a tiny value to force a breach.
    /// </summary>
    [Range(0.0000001, 1_000_000.0)]
    public decimal GlobalCeilingUsd { get; init; }

    public CostPrices ToCostPrices() => new(
        SonnetInputPerMTok,
        SonnetOutputPerMTok,
        HaikuInputPerMTok,
        HaikuOutputPerMTok,
        GeminiPerImage);
}
