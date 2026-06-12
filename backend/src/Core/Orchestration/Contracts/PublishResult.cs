namespace Backend.Core.Orchestration.Contracts;

public sealed record PublishResult(
    string? ExternalRef,
    string Status,
    string? Error);
