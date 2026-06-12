namespace Backend.Core.Orchestration;

public sealed record Budget(
    int TokenBudget,
    int TokensSpent,
    decimal MediaBudget,
    decimal MediaSpent);
