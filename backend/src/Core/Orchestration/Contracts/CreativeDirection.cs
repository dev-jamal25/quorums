namespace Backend.Core.Orchestration.Contracts;

public sealed record CreativeDirection(
    string VisualConcept,
    List<string> StyleTokens,
    List<string> ColorTokens,
    string MediaPromptBrief);
