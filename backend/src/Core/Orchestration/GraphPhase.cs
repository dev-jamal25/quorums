using System.Text.Json.Serialization;

namespace Backend.Core.Orchestration;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GraphPhase
{
    Strategy,
    Creative,
    Generation,
    Assembled,
    AwaitingApproval,
    Publishing,
    Done,
}
