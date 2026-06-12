using System.Text.Json;
using System.Text.Json.Serialization;

namespace Backend.Infrastructure.Jobs;

internal static class RunStateJsonOptions
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
