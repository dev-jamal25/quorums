using Backend.Core.Orchestration;
using Backend.Infrastructure.Configuration.Options;
using Backend.Infrastructure.Tracing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Backend.IntegrationTests.Tracing;

/// <summary>
/// The generation-instrumentation gate: the delegating <see cref="LangfuseChatClient"/> records each
/// LLM call as a Langfuse generation (model + token usage) on the ambient run's trace, and stays
/// best-effort + config-gated — outside a run, or with Langfuse off (the local recorder), nothing is
/// posted, and a failed post never fails the call.
/// </summary>
[Trait("Category", "Trace")]
public sealed class GenerationTracingTests
{
    private sealed class CapturingHandler(Func<HttpResponseMessage>? responder = null) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return (responder ?? (() => new HttpResponseMessage(System.Net.HttpStatusCode.OK)))();
        }
    }

    private sealed class StubChatClient(ChatResponse response) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(response);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static ChatResponse StubResponse() =>
        new(new ChatMessage(ChatRole.Assistant, "the assistant output"))
        {
            Usage = new UsageDetails { InputTokenCount = 123, OutputTokenCount = 45 },
        };

    private static LangfuseTrace Tracer(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://langfuse.test/") },
            Options.Create(new LangfuseOptions()),
            NullLogger<LangfuseTrace>.Instance);

    [Fact]
    public async Task LLM_call_emits_a_generation_with_model_and_token_usage_on_the_run_trace()
    {
        var runId = Guid.NewGuid();
        var brandId = Guid.NewGuid();
        var handler = new CapturingHandler();
        var client = new LangfuseChatClient(new StubChatClient(StubResponse()), Tracer(handler));

        using (RunTraceScope.Begin(runId, brandId))
        {
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "the user prompt")],
                new ChatOptions { ModelId = "claude-sonnet-test" });
            Assert.Equal("the assistant output", response.Text); // the inner response is passed through
        }

        var body = Assert.Single(handler.Bodies);
        Assert.Contains("generation-create", body);
        Assert.Contains("claude-sonnet-test", body);   // model id populated
        Assert.Contains("\"input\":123", body);          // input token usage
        Assert.Contains("\"output\":45", body);          // output token usage
        Assert.Contains(runId.ToString("N"), body);      // attached to THIS run's trace
    }

    [Fact]
    public async Task No_run_context_records_no_generation()
    {
        var handler = new CapturingHandler();
        var client = new LangfuseChatClient(new StubChatClient(StubResponse()), Tracer(handler));

        // No RunTraceScope -> the call is not inside a run, so no orphan generation is posted.
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], new ChatOptions { ModelId = "m" });

        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task A_failed_post_is_swallowed_and_the_call_still_returns()
    {
        var handler = new CapturingHandler(() => throw new HttpRequestException("langfuse down"));
        var client = new LangfuseChatClient(new StubChatClient(StubResponse()), Tracer(handler));

        using (RunTraceScope.Begin(Guid.NewGuid(), Guid.NewGuid()))
        {
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "x")], new ChatOptions { ModelId = "m" });
            Assert.Equal("the assistant output", response.Text); // tracing failure never fails the call
        }
    }

    [Fact]
    public async Task Local_recorder_posts_nothing_when_langfuse_is_unconfigured()
    {
        // Langfuse off -> ITrace is the local recorder, which no-ops generations (no HTTP at all).
        var client = new LangfuseChatClient(new StubChatClient(StubResponse()), new LocalTraceRecorder());

        using (RunTraceScope.Begin(Guid.NewGuid(), Guid.NewGuid()))
        {
            var response = await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "x")], new ChatOptions { ModelId = "m" });
            Assert.Equal("the assistant output", response.Text);
        }
    }
}
