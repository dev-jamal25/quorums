namespace Backend.Core.Orchestration;

public sealed record ToolError(
    string Code,
    string Message,
    bool Retryable);
