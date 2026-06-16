using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic.SDK;
using Backend.Core.Generation;
using Backend.Infrastructure.Generation;
using Microsoft.Extensions.AI;
using Xunit;

namespace Backend.IntegrationTests.Generation;

/// <summary>
/// Deterministic, <b>offline</b> counterpart to <see cref="ForcedToolSpikeTests"/> (which proves the
/// same thing but needs a live key, so it is skipped in CI). It drives the real production forcing
/// path — <see cref="ForcedToolGenerator"/> over the Anthropic.SDK <c>AnthropicClient.Messages</c>
/// <see cref="IChatClient"/> — through a short-circuiting handler that captures the outgoing request
/// and returns a canned <c>tool_use</c>. It pins the two adapter quirks the spike found (DL-034 R4):
/// a forced <c>tool_choice</c> is serialized onto the wire, and the <em>invokable</em> tool is NOT
/// dropped from the <c>tools</c> array. A future Microsoft.Extensions.AI / Anthropic.SDK bump that
/// silently stopped forwarding forcing would otherwise pass every other test — this fails loudly.
/// This is the one place Anthropic.SDK is touched outside Infrastructure (DL-032), to probe the wire.
/// </summary>
[Trait("Category", "Generation")]
public sealed class ForcedToolForwardingTests
{
    private const string ToolName = "record_probe";

    /// <summary>Throwaway target — the forced tool's input schema is generated from this record (R4).</summary>
    private sealed record Probe(string City);

    [Fact]
    public async Task ForcedToolGenerator_serializes_tool_choice_and_the_invokable_tool_onto_the_wire()
    {
        var handler = new ShortCircuitHandler(CannedToolUse());
        using var httpClient = new HttpClient(handler);
        IChatClient chat = new AnthropicClient(new APIAuthentication("offline-not-used"), httpClient).Messages;
        var generator = new ForcedToolGenerator(chat);

        var request = new StructuredGenerationRequest<Probe>(
            Prompt: "What is the capital of France? Reply conversationally in a full sentence.",
            ToolName: ToolName,
            ToolDescription: "Record the probe answer as structured fields.",
            ModelId: "claude-haiku-4-5",
            Validate: _ => ValidationResult.Valid)
        {
            MaxRetries = 0, // single call site — the canned response is valid, so no retry is needed.
        };

        GenerationOutcome<Probe>? outcome = null;
        Exception? thrown = null;
        try
        {
            outcome = await generator.GenerateAsync(request, CancellationToken.None);
        }
        catch (Exception ex)
        {
            thrown = ex; // captured so the primary (request-forwarding) assertion still runs.
        }

        // PRIMARY — the forced tool_choice reached the wire (R4 path (a) still forwards).
        var outgoing = handler.LastRequestBody;
        Assert.NotNull(outgoing);
        using var doc = JsonDocument.Parse(outgoing!);
        Assert.True(
            doc.RootElement.TryGetProperty("tool_choice", out var toolChoice),
            "regression: the Anthropic.SDK IChatClient adapter no longer forwards a forced tool_choice.");
        Assert.Equal("tool", toolChoice.GetProperty("type").GetString());
        Assert.Equal(ToolName, toolChoice.GetProperty("name").GetString());

        // PRIMARY — the invokable tool is in the wire tools array (declaration-only tools were dropped).
        Assert.True(
            doc.RootElement.TryGetProperty("tools", out var tools) && tools.GetArrayLength() > 0,
            "regression: the forced tool was dropped from the request tools array.");
        Assert.Contains(
            tools.EnumerateArray(),
            tool => string.Equals(tool.GetProperty("name").GetString(), ToolName, StringComparison.Ordinal));

        // SECONDARY — the canned tool_use round-trips through the real adapter into the typed record.
        Assert.Null(thrown);
        Assert.NotNull(outcome);
        Assert.True(outcome!.Succeeded, outcome.Error?.Message);
        Assert.Equal("Paris", outcome.Value!.City);
    }

    /// <summary>A minimal, valid Anthropic Messages <c>tool_use</c> response (offline canned).</summary>
    private static string CannedToolUse() =>
        """
        {
          "id": "msg_offline",
          "type": "message",
          "role": "assistant",
          "model": "claude-haiku-4-5",
          "content": [
            { "type": "tool_use", "id": "toolu_offline", "name": "record_probe", "input": { "city": "Paris" } }
          ],
          "stop_reason": "tool_use",
          "stop_sequence": null,
          "usage": { "input_tokens": 10, "output_tokens": 5 }
        }
        """;

    /// <summary>Captures the outgoing request body and returns the canned response — never hits the network.</summary>
    private sealed class ShortCircuitHandler(string responseJson) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }
}
