using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Core.Orchestration.Contracts;
using Microsoft.Extensions.AI;
using Xunit;

namespace Backend.UnitTests.Generation;

/// <summary>
/// Record-first schema derivation (DL-028, R4): the JSON schema handed to the forced tool is
/// GENERATED from the canonical C# record — never a hand-maintained dual. These tests prove the
/// generated schema reflects the record's fields, and that a value round-trips record → JSON
/// (the tool input shape) → record, including the string enums and nested grounding.
/// </summary>
public sealed class SchemaRoundTripTests
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void Generated_schema_reflects_the_ContentStrategy_record_fields()
    {
#pragma warning disable MEAI001 // AIJsonUtilities schema surface is experimental in MEAI.
        var schema = AIJsonUtilities.CreateJsonSchema(typeof(ContentStrategy), serializerOptions: _json);
#pragma warning restore MEAI001

        Assert.Equal("object", schema.GetProperty("type").GetString());
        var properties = schema.GetProperty("properties");

        // Derived from the record's parameter names (Web casing) — no hand-kept second copy.
        foreach (var field in new[] { "pillar", "angle", "objective", "audience", "angleRationale", "grounding" })
        {
            Assert.True(properties.TryGetProperty(field, out _), $"schema missing '{field}'");
        }
    }

    [Fact]
    public void ContentStrategy_round_trips_through_the_tool_input_shape()
    {
        var original = new ContentStrategy(
            Pillar: "Origin",
            Angle: "single-origin provenance",
            Objective: Objective.Conversion,
            Audience: "home brewers",
            AngleRationale: "ties the product to the brand's craft pillar",
            CalendarSlot: null,
            Grounding: new Grounding(Grounded: true, ChunkIdsUsed: ["prod_017", "pb_mission_03"], Confidence.High));

        var json = JsonSerializer.Serialize(original, _json);
        var parsed = JsonSerializer.Deserialize<ContentStrategy>(json, _json);

        Assert.NotNull(parsed);
        Assert.Equal(original.Pillar, parsed!.Pillar);
        Assert.Equal(Objective.Conversion, parsed.Objective);          // enum survives as a string
        Assert.Equal(Confidence.High, parsed.Grounding.Confidence);
        Assert.True(parsed.Grounding.Grounded);
        Assert.Equal(original.Grounding.ChunkIdsUsed, parsed.Grounding.ChunkIdsUsed);
    }

    [Fact]
    public void StrategyCandidates_envelope_round_trips_with_three_candidates()
    {
        var grounding = new Grounding(false, [], Confidence.Low);
        var candidate = new ContentStrategy("Craft", "a", Objective.Awareness, "aud", "r", null, grounding);
        var original = new StrategyCandidates([candidate, candidate, candidate]);

        var json = JsonSerializer.Serialize(original, _json);
        var parsed = JsonSerializer.Deserialize<StrategyCandidates>(json, _json);

        Assert.NotNull(parsed);
        Assert.Equal(3, parsed!.Candidates.Count);
        Assert.Equal("Craft", parsed.Candidates[0].Pillar);
    }
}
