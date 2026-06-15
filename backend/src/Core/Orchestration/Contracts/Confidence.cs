namespace Backend.Core.Orchestration.Contracts;

/// <summary>
/// The model's self-reported grounding confidence on a <see cref="Grounding"/> (DL-028). It may
/// be model-set and is reported alongside the validated chunk-id count — but it never decides
/// <see cref="Grounding.Grounded"/>, which is derived from the validated provenance intersection
/// (DL-034 R6), not trusted from the model.
/// </summary>
public enum Confidence
{
    Low,
    Medium,
    High,
}
