namespace Backend.Core.Orchestration.Contracts;

/// <summary>
/// The fixed marketing objective enum carried by a <see cref="ContentStrategy"/> (DL-028).
/// Unlike <c>pillar</c> (a free string validated against the brand's playbook pillars), this is
/// a closed set — a value outside it is a schema violation, not a regenerate-on-pillar case.
/// </summary>
public enum Objective
{
    Awareness,
    Engagement,
    Conversion,
    Traffic,
    Retention,
}
