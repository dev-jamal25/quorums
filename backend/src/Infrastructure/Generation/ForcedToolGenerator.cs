using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Core.Generation;
using Backend.Core.Orchestration;
using Microsoft.Extensions.AI;

namespace Backend.Infrastructure.Generation;

/// <summary>
/// The structured-output seam (DL-028, DL-034 R4), decided by the STEP A spike: schema is
/// GENERATED from the record (<see cref="AIJsonUtilities"/>), attached to an <em>invokable</em>
/// <see cref="AIFunction"/> (a declaration-only tool is dropped by the Anthropic.SDK adapter),
/// and forced via <see cref="ChatToolMode.RequireSpecific(string)"/> through the injected
/// <see cref="IChatClient"/>. The returned <c>tool_use</c> input is deserialized into the record.
///
/// <para>A forwarded forced tool_choice only guarantees a tool_use with schema-<em>guided</em>
/// input — Anthropic does not hard-validate the schema — so this loop validates on receipt and
/// retries up to <see cref="StructuredGenerationRequest{T}.MaxRetries"/> times, feeding the
/// <b>specific</b> error back, before returning a <see cref="ToolError"/>. It never throws into
/// the graph (DL-022). The Anthropic.SDK type stays in Infrastructure (DL-032).</para>
/// </summary>
public sealed class ForcedToolGenerator : IStructuredGenerator
{
    // Web casing + string enums so the generated schema and the deserialization agree (the model
    // emits enum members as strings, matching the schema this same options object produces).
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IChatClient _chat;

    public ForcedToolGenerator(IChatClient chat) => _chat = chat;

    public async Task<GenerationOutcome<T>> GenerateAsync<T>(
        StructuredGenerationRequest<T> request,
        CancellationToken cancellationToken = default)
        where T : class
    {
#pragma warning disable MEAI001 // AIJsonUtilities schema surface is experimental in MEAI.
        var schema = AIJsonUtilities.CreateJsonSchema(typeof(T), serializerOptions: _json);
#pragma warning restore MEAI001
        AITool tool = new SchemaTool(request.ToolName, request.ToolDescription, schema);

        var options = new ChatOptions
        {
            ModelId = request.ModelId,
            MaxOutputTokens = request.MaxOutputTokens,
            Tools = [tool],
            ToolMode = ChatToolMode.RequireSpecific(request.ToolName),
        };

        // No assistant tool_use turns are echoed back: corrections are plain user turns, which
        // avoids the "every tool_use needs a tool_result" rejection while still feeding the error.
        var messages = new List<ChatMessage> { new(ChatRole.User, request.Prompt) };
        var lastError = "the model did not call the required tool";

        // One initial attempt plus the bounded retries (DL-027/028).
        for (var attempt = 0; attempt <= request.MaxRetries; attempt++)
        {
            var response = await _chat.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

            var call = response.Messages
                .SelectMany(message => message.Contents)
                .OfType<FunctionCallContent>()
                .FirstOrDefault(content => string.Equals(content.Name, request.ToolName, StringComparison.Ordinal));

            if (call is null)
            {
                lastError = "the model did not call the required tool";
            }
            else if (!TryDeserialize<T>(call, out var value, out var deserializeError))
            {
                lastError = deserializeError;
            }
            else
            {
                var validation = request.Validate(value);
                if (validation.IsValid)
                {
                    return GenerationOutcome.Ok(value);
                }

                lastError = validation.Error ?? "validation failed";
            }

            if (attempt < request.MaxRetries)
            {
                messages.Add(new ChatMessage(
                    ChatRole.User,
                    $"Your previous response was rejected: {lastError}. Call the {request.ToolName} " +
                    "tool again and return only corrected, schema-valid fields."));
            }
        }

        // Retries exhausted: a typed error, never an exception (DL-022). The Supervisor's
        // deterministic control plane degrades or fails per the DL-022/023 node policy.
        return GenerationOutcome.Fail<T>(new ToolError(
            Code: "generation.schema_violation",
            Message: $"structured output invalid after {request.MaxRetries} retries: {lastError}",
            Retryable: false));
    }

    private static bool TryDeserialize<T>(FunctionCallContent call, out T value, out string error)
        where T : class
    {
        try
        {
            var element = JsonSerializer.SerializeToElement(call.Arguments, _json);
            var parsed = element.Deserialize<T>(_json);
            if (parsed is null)
            {
                value = null!;
                error = "the tool input deserialized to null";
                return false;
            }

            value = parsed;
            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            value = null!;
            error = $"the tool input did not match the schema: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// An invokable <see cref="AIFunction"/> whose input schema is the record-derived schema.
    /// It must be invokable (not <c>AIFunctionFactory.CreateDeclaration</c>): the STEP A spike
    /// showed the adapter drops declaration-only tools from the wire <c>tools</c> array, yielding
    /// a 400 "Tool not found in provided tools". It is never invoked — the seam reads the tool_use.
    /// </summary>
    private sealed class SchemaTool(string name, string description, JsonElement schema) : AIFunction
    {
        public override string Name { get; } = name;

        public override string Description { get; } = description;

        public override JsonElement JsonSchema { get; } = schema;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            throw new NotSupportedException("forced-tool declaration — the tool_use is read, never invoked.");
    }
}
