using System.Text.Json.Serialization;

namespace Backend.Api.Dtos;

/// <summary>
/// The human gate decision (DL-041). Decision-discriminated: <c>approve</c> may carry caption/hashtag
/// edits and an optional schedule; <c>reject</c> carries an optional reason; <c>regenerate</c> carries
/// a required <c>mode</c> (<c>same-angle</c> | <c>reselect-angle</c>) + an optional reason (DL-036).
/// The decision enum binds from the JSON string (e.g. <c>"approve"</c>) via the targeted converter —
/// no global JSON change. <c>Mode</c> stays a kebab string to match the wire form.
/// </summary>
public sealed record ApprovalRequest(
    GateDecision Decision,
    ApprovalEdits? Edits,
    DateTimeOffset? ScheduledFor,
    string? Reason,
    string? Mode = null);

/// <summary>The editable surface at the gate (DL-035): caption text and hashtags only. Null = publish as-is.</summary>
public sealed record ApprovalEdits(string? Caption, IReadOnlyList<string>? Hashtags);

/// <summary>The kebab wire values for the regenerate <c>mode</c> (DL-036).</summary>
public static class RegenerateModes
{
    public const string SameAngle = "same-angle";
    public const string ReselectAngle = "reselect-angle";

    public static readonly string[] All = [SameAngle, ReselectAngle];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GateDecision
{
    Approve,
    Reject,
    Regenerate,
}
