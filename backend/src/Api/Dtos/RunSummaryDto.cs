using Backend.Core.Domain;

namespace Backend.Api.Dtos;

/// <summary>One row in the run dashboard list — identity, lifecycle status, and timestamps. Brand-scoped
/// (RLS); the dashboard renders these and links into the per-run review.</summary>
public sealed record RunSummaryDto(
    Guid RunId,
    RunStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
