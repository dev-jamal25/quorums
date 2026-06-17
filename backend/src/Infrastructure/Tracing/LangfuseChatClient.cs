using Backend.Core.Orchestration;
using Microsoft.Extensions.AI;

namespace Backend.Infrastructure.Tracing;

/// <summary>
/// Delegating <see cref="IChatClient"/> that records each completed LLM call as a Langfuse generation
/// — the model (<see cref="ChatOptions.ModelId"/>) and input/output token usage
/// (<see cref="ChatResponse.Usage"/>, which Langfuse turns into cost) — attached to the ambient run's
/// trace (<see cref="RunTraceScope"/>). Best-effort + config-gated: outside a run (no context) nothing
/// is recorded, with Langfuse off the local recorder no-ops the generation, and a failed post never
/// fails the call. Wraps the single registered <c>IChatClient</c> (live Anthropic or the deterministic
/// CI client), so no agent/node code changes.
/// </summary>
public sealed class LangfuseChatClient : DelegatingChatClient
{
    private const string GenerationName = "llm-generation";

    private readonly ITrace _trace;

    public LangfuseChatClient(IChatClient innerClient, ITrace trace)
        : base(innerClient) => _trace = trace;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        if (RunTraceScope.Current is { } context)
        {
            await _trace.RecordGenerationAsync(
                context.RunId,
                context.BrandId,
                GenerationName,
                options?.ModelId,
                response.Usage?.InputTokenCount,
                response.Usage?.OutputTokenCount,
                FirstUserText(messages),
                response.Text,
                startedAt,
                DateTimeOffset.UtcNow,
                cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    // Only the user turn is set as the generation input (not every arg), per the masking guidance.
    private static string? FirstUserText(IEnumerable<ChatMessage> messages) =>
        messages.FirstOrDefault(m => m.Role == ChatRole.User)?.Text;
}
