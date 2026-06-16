using System.Text.Json.Serialization;

namespace Backend.Api.Dtos;

/// <summary>
/// The human gate decision (DL-041). Decision-discriminated: <c>approve</c> may carry caption/hashtag
/// edits and an optional schedule; <c>reject</c> carries an optional reason. <c>Regenerate</c> ships in
/// Slice 5 with its graph re-entry loop. The enum binds from the JSON string (e.g. <c>"approve"</c>)
/// via the targeted converter — no global JSON change.
/// </summary>
public sealed record ApprovalRequest(
    GateDecision Decision,
    ApprovalEdits? Edits,
    DateTimeOffset? ScheduledFor,
    string? Reason);

/// <summary>The editable surface at the gate (DL-035): caption text and hashtags only. Null = publish as-is.</summary>
public sealed record ApprovalEdits(string? Caption, IReadOnlyList<string>? Hashtags);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GateDecision
{
    Approve,
    Reject,
}
