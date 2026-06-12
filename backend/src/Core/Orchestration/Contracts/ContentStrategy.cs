namespace Backend.Core.Orchestration.Contracts;

public sealed record ContentStrategy(
    string Pillar,
    string Angle,
    string Objective,
    string Audience,
    string? CalendarSlot);
