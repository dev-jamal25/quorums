using System.Text.Json.Serialization;

namespace Backend.Core.Orchestration.Contracts;

/// <summary>
/// The content modality chosen per run (DL-058 Decision 1): an image post or a video post. Selected on
/// <c>POST /runs</c>, persisted on the <c>AgentRun</c> row (so a retry rebuilds the same modality through
/// Postgres, never the job payload — DL-006), and read by <c>ExecuteRun</c> into <c>RunState</c>. Binds
/// from / serializes to its JSON string name (e.g. <c>"Video"</c>) via the targeted converter, matching
/// the <see cref="VideoSource"/> pattern — no global JSON change.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Modality
{
    Image = 0,
    Video = 1,
}
