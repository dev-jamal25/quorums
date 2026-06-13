namespace Backend.Core.Orchestration.Contracts;

public sealed record Caption(
    string Hook,
    string Body,
    List<string> Hashtags);
