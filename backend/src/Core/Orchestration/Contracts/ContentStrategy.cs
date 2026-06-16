namespace Backend.Core.Orchestration.Contracts;

/// <summary>
/// A single candidate content strategy — the Content Strategist owns <em>what to say</em>
/// (DL-027/028). <see cref="Pillar"/> is a free string validated against the brand's playbook
/// pillars at receipt (DL-026); <see cref="Objective"/> is the fixed <see cref="Contracts.Objective"/>
/// enum; <see cref="AngleRationale"/> is the one line that drives the Supervisor's selection and
/// persists in the trace. Every output carries <see cref="Contracts.Grounding"/>.
/// </summary>
public sealed record ContentStrategy(
    string Pillar,
    string Angle,
    Objective Objective,
    string Audience,
    string AngleRationale,
    string? CalendarSlot,
    Grounding Grounding);
