using System.Text.Json.Serialization;

namespace Backend.Core.Orchestration;

/// <summary>
/// A human-gate action a reviewer may take on a run (DL-041). The server computes which of these are
/// currently legal (<see cref="GateActionPolicy"/>) and returns the list on the review DTO; the client
/// renders that list verbatim and NEVER recomputes gate policy. Edit + schedule are sub-capabilities of
/// <see cref="Approve"/> (always available when approve is), not separate actions.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GateAction
{
    Approve,
    Reject,
    Regenerate,
    Cancel,
}
