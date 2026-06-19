using Backend.Infrastructure.Configuration.Options;
using Backend.Infrastructure.Integrations.Gemini;
using Backend.IntegrationTests.Support;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Xunit;

namespace Backend.IntegrationTests.Eval;

/// <summary>
/// The slice-6 hardening: the judge <see cref="GeminiChatClient"/> handles a safety-filtered / empty
/// Gemini response with a CLEAR typed diagnostic (<see cref="GeminiJudgeBlockedException"/>), not a silent
/// empty response or an unhandled crash. Driven over a recording HTTP handler — a canned blocked payload,
/// no live Gemini call, deterministic, no spend. So the manual live re-calibration fails cleanly on a
/// blocked adversarial item instead of mis-reading it as "unparseable".
/// </summary>
[Trait("Category", "Eval")]
[Trait("Category", "EvalGate")]
public sealed class GeminiJudgeClientTests
{
    private static GeminiChatClient Client(RecordingHttpMessageHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("http://gemini.test/") },
            Options.Create(new GeminiOptions { ApiKey = "x", BaseUrl = "http://gemini.test", JudgeModel = "gemini-2.5-flash" }));

    private static readonly ChatMessage[] _prompt = [new(ChatRole.User, "judge this")];

    [Fact]
    public async Task Prompt_level_safety_block_throws_a_clear_typed_diagnostic()
    {
        // promptFeedback.blockReason set, no candidates → no text.
        var handler = new RecordingHttpMessageHandler("""{"promptFeedback":{"blockReason":"SAFETY"},"candidates":[]}""");

        var ex = await Assert.ThrowsAsync<GeminiJudgeBlockedException>(() => Client(handler).GetResponseAsync(_prompt));
        Assert.Contains("SAFETY", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_candidate_with_a_safety_finish_reason_throws()
    {
        var handler = new RecordingHttpMessageHandler("""{"candidates":[{"finishReason":"SAFETY","content":{"parts":[]}}]}""");

        var ex = await Assert.ThrowsAsync<GeminiJudgeBlockedException>(() => Client(handler).GetResponseAsync(_prompt));
        Assert.Contains("SAFETY", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_normal_response_returns_the_candidate_text()
    {
        var handler = new RecordingHttpMessageHandler("""{"candidates":[{"content":{"parts":[{"text":"verdict text"}]},"finishReason":"STOP"}]}""");

        var response = await Client(handler).GetResponseAsync(_prompt);
        Assert.Equal("verdict text", response.Text);
    }
}
