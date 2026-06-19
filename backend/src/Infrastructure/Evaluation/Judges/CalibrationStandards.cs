using Backend.Core.Domain;
using Backend.Infrastructure.Knowledge.Seed;

namespace Backend.Infrastructure.Evaluation.Judges;

/// <summary>
/// Loads the demo brand's real standards from the seeded <see cref="CoffeeRoasterCorpus"/> so the judges
/// cite genuine brand documents, not invented rubrics (DL-057, Step 0(5)). Brand standards = the
/// BrandPlaybook docs (Voice, Audience Persona, Mission, Visual Style); grounding context = the full
/// factual corpus (products, intel, posts, platform guidance) the output's claims must be supported by.
/// </summary>
public static class CalibrationStandards
{
    public static string BrandStandards() =>
        string.Join("\n\n", CoffeeRoasterCorpus.Specs
            .Where(spec => spec.DocType == DocType.BrandPlaybook)
            .Select(spec => $"{spec.Title}: {spec.Content}"));

    public static string GroundingContext() =>
        string.Join("\n\n", CoffeeRoasterCorpus.Specs.Select(spec => $"{spec.Title}: {spec.Content}"));
}
