using System.Text.Json;
using Anthropic.SDK;
using Microsoft.Extensions.AI;
using Xunit;
using Xunit.Abstractions;

namespace Backend.IntegrationTests.Generation;

/// <summary>
/// STEP A — structured-output spike (DL-034 R4). Decides the generation-pipeline
/// structured-output seam by making real calls through the registered <see cref="IChatClient"/>
/// (Anthropic.SDK <c>AnthropicClient.Messages</c>) and confirming BOTH: (1) the OUTGOING
/// serialized request carries <c>tool_choice:{type:"tool",name:...}</c> (forced tool forwards),
/// and (2) the returned <c>tool_use</c> input deserializes into a throwaway C# record.
///
/// <para>Throwaway dev harness, gated on a live key (Trait Category=Spike — excluded from CI,
/// which runs on mocks). This is the ONE place Anthropic.SDK is touched outside Infrastructure
/// (DL-032), because its whole purpose is to probe the SDK's wire behaviour.</para>
///
/// <para>Two probes inform the STEP C seam:
/// <list type="bullet">
///   <item><b>Probe A</b> — an <em>invokable</em> forced <see cref="AIFunction"/> whose schema is
///   GENERATED from the record (R4) + <see cref="ChatToolMode.RequireSpecific(string)"/>. This is
///   the decisive R4 test. (A declaration-only tool is dropped by the adapter — see the comment
///   in <see cref="RecordTool"/>.)</item>
///   <item><b>Probe B</b> — the supported record-first <c>GetResponseAsync&lt;T&gt;</c>, logged
///   for comparison.</item>
/// </list></para>
///
/// A forwarded forced tool_choice only guarantees a tool_use with schema-GUIDED input — Anthropic
/// does not hard-validate the schema — so the STEP C retries + field validators stay load-bearing.
/// </summary>
[Trait("Category", "Spike")]
public sealed class ForcedToolSpikeTests
{
    private static readonly JsonSerializerOptions _webJson = new(JsonSerializerDefaults.Web);

    private readonly ITestOutputHelper _output;

    public ForcedToolSpikeTests(ITestOutputHelper output) => _output = output;

    /// <summary>Throwaway target: the forced tool's input schema is GENERATED from this record (R4).</summary>
    private sealed record SpikeAnswer(string City, int ConfidencePercent);

    [Fact]
    public async Task Forced_tool_choice_forwards_to_the_wire_and_tool_use_deserializes()
    {
        var apiKey = Environment.GetEnvironmentVariable("Anthropic__ApiKey")
                     ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // xUnit v2 has no dynamic Assert.Skip; the Category=Spike trait keeps this out of CI.
            _output.WriteLine("SKIP: no Anthropic__ApiKey / ANTHROPIC_API_KEY — spike needs a live key.");
            return;
        }

        var capture = new CapturingHandler { InnerHandler = new HttpClientHandler() };
        using var httpClient = new HttpClient(capture);
        IChatClient chat = new AnthropicClient(new APIAuthentication(apiKey), httpClient).Messages;

        const string toolName = "record_answer";

        // R4: schema GENERATED from the canonical record — never a hand-maintained dual.
#pragma warning disable MEAI001 // AIJsonUtilities schema surface is experimental in MEAI.
        JsonElement schema = AIJsonUtilities.CreateJsonSchema(typeof(SpikeAnswer));
#pragma warning restore MEAI001

        // ---- Probe A: invokable forced tool + RequireSpecific (the decisive R4 path) ----
        AITool tool = new RecordTool(toolName, "Record the answer as structured fields.", schema);
        var optionsA = new ChatOptions
        {
            ModelId = "claude-haiku-4-5",       // spike-only literal; production model ids are config-bound
            MaxOutputTokens = 512,
            Tools = [tool],
            ToolMode = ChatToolMode.RequireSpecific(toolName),
        };

        // A prompt that would otherwise warrant a PROSE reply — a text answer would prove forcing failed.
        const string prompt = "What is the capital of France? Reply conversationally in a full sentence.";
        ChatResponse response = await chat
            .GetResponseAsync(prompt, optionsA, CancellationToken.None)
            .ConfigureAwait(true);

        // (1) OUTGOING — inspect the serialized request actually sent on the wire.
        var outgoing = capture.LastRequestBody;
        _output.WriteLine("=== PROBE A — OUTGOING REQUEST BODY ===");
        _output.WriteLine(outgoing ?? "(null)");
        Assert.NotNull(outgoing);
        using (var doc = JsonDocument.Parse(outgoing!))
        {
            Assert.True(
                doc.RootElement.TryGetProperty("tool_choice", out var toolChoice),
                "OUTGOING request carried no tool_choice — the forced tool did NOT forward (R4 path (a) fails).");
            Assert.Equal("tool", toolChoice.GetProperty("type").GetString());
            Assert.Equal(toolName, toolChoice.GetProperty("name").GetString());
        }

        // (2) INCOMING — the tool_use input deserializes into the throwaway record.
        var call = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .FirstOrDefault(c => string.Equals(c.Name, toolName, StringComparison.Ordinal));
        Assert.NotNull(call);
        var argsElement = JsonSerializer.SerializeToElement(call!.Arguments);
        var parsed = argsElement.Deserialize<SpikeAnswer>(_webJson);
        _output.WriteLine("=== PROBE A — DESERIALIZED tool_use input ===");
        _output.WriteLine(JsonSerializer.Serialize(parsed));
        Assert.NotNull(parsed);
        Assert.False(string.IsNullOrWhiteSpace(parsed!.City));

        // Forcing proof: the model emitted a tool call rather than prose.
        Assert.True(
            string.IsNullOrWhiteSpace(response.Text),
            "model produced prose text — forcing likely failed.");

        // ---- Probe B: supported record-first GetResponseAsync<T> (informational, for STEP C) ----
        capture.Reset();
        try
        {
            var typed = await chat
                .GetResponseAsync<SpikeAnswer>(
                    prompt, new ChatOptions { ModelId = "claude-haiku-4-5", MaxOutputTokens = 512 },
                    true, CancellationToken.None)
                .ConfigureAwait(true);
            _output.WriteLine("=== PROBE B — OUTGOING REQUEST BODY (GetResponseAsync<T>) ===");
            _output.WriteLine(capture.LastRequestBody ?? "(null)");
            _output.WriteLine("=== PROBE B — TryGetResult ===");
            _output.WriteLine(typed.TryGetResult(out var r) ? JsonSerializer.Serialize(r) : "(no result)");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"=== PROBE B — threw: {ex.GetType().Name}: {ex.Message} ===");
            _output.WriteLine($"PROBE B outgoing was: {capture.LastRequestBody ?? "(null)"}");
        }
    }

    /// <summary>
    /// Minimal invokable <see cref="AIFunction"/> whose input schema is the record-derived schema.
    /// It is invokable (not <c>AIFunctionFactory.CreateDeclaration</c>): the Anthropic.SDK
    /// IChatClient adapter serialises invokable functions into the request <c>tools</c> array, but
    /// drops declaration-only tools — a declaration yields "Tool '…' not found in provided tools".
    /// Never invoked here: the seam reads the returned tool_use, it does not execute the tool.
    /// </summary>
    private sealed class RecordTool(string name, string description, JsonElement schema) : AIFunction
    {
        public override string Name { get; } = name;
        public override string Description { get; } = description;
        public override JsonElement JsonSchema { get; } = schema;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            throw new NotSupportedException("declaration tool — read the tool_use; never invoked.");
    }

    /// <summary>Records the last outgoing request body so the spike can inspect the wire (R4).</summary>
    private sealed class CapturingHandler : DelegatingHandler
    {
        public string? LastRequestBody { get; private set; }

        public void Reset() => LastRequestBody = null;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content
                    .ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
