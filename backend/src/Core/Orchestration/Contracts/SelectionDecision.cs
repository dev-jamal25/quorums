namespace Backend.Core.Orchestration.Contracts;

/// <summary>
/// The Supervisor's multi-angle selection result (DL-027/028): the index of the chosen
/// <see cref="ContentStrategy"/> candidate plus a one-line rationale. <see cref="ChosenIndex"/>
/// must be in <c>[0, N)</c> — out of range is a schema violation that drives a retry then a
/// <c>ToolError</c> (DL-034 R5).
/// </summary>
public sealed record SelectionDecision(int ChosenIndex, string Rationale);
