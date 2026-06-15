using Backend.Core.Generation;
using Backend.Infrastructure.Generation;
using Xunit;

namespace Backend.UnitTests.Generation;

/// <summary>
/// The validate-on-receipt + bounded-retry loop (DL-027/028) over the deterministic
/// <see cref="QueuedChatClient"/>. Proves: a valid output is accepted; an invalid-then-valid
/// sequence retries with the specific error fed back; and an exhausted retry budget returns a
/// <c>ToolError</c> rather than throwing (DL-022). No real Claude call.
/// </summary>
public sealed class ForcedToolGeneratorTests
{
    private sealed record SampleOut(string Name, int Score);

    private const string ToolName = "record_sample";

    private static StructuredGenerationRequest<SampleOut> Request(Func<SampleOut, ValidationResult> validate) =>
        new(
            Prompt: "produce a sample",
            ToolName: ToolName,
            ToolDescription: "record a sample",
            ModelId: "test-model",
            Validate: validate);

    private static ValidationResult ScoreInRange(SampleOut output) =>
        output.Score is >= 0 and <= 10
            ? ValidationResult.Valid
            : ValidationResult.Invalid($"score={output.Score} out of range [0, 10]");

    [Fact]
    public async Task Valid_first_attempt_is_accepted_without_retry()
    {
        var chat = new QueuedChatClient(
            _ => QueuedChatClient.ToolCall(ToolName, new { name = "ok", score = 5 }));
        var generator = new ForcedToolGenerator(chat);

        var outcome = await generator.GenerateAsync(Request(ScoreInRange));

        Assert.True(outcome.Succeeded);
        Assert.Equal("ok", outcome.Value!.Name);
        Assert.Equal(5, outcome.Value.Score);
        Assert.Single(chat.Calls);
    }

    [Fact]
    public async Task Invalid_then_valid_retries_and_feeds_the_specific_error_back()
    {
        var chat = new QueuedChatClient(
            _ => QueuedChatClient.ToolCall(ToolName, new { name = "bad", score = 99 }),   // invalid
            _ => QueuedChatClient.ToolCall(ToolName, new { name = "good", score = 7 }));   // valid
        var generator = new ForcedToolGenerator(chat);

        var outcome = await generator.GenerateAsync(Request(ScoreInRange));

        Assert.True(outcome.Succeeded);
        Assert.Equal("good", outcome.Value!.Name);
        Assert.Equal(2, chat.Calls.Count);

        // The retry call carries a message feeding the concrete validation error back.
        var retryText = string.Join("\n", chat.Calls[1].Select(message => message.Text));
        Assert.Contains("score=99 out of range", retryText, StringComparison.Ordinal);
        Assert.Contains(ToolName, retryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_through_all_retries_returns_a_ToolError_not_an_exception()
    {
        var chat = new QueuedChatClient(
            _ => QueuedChatClient.ToolCall(ToolName, new { name = "a", score = 50 }),
            _ => QueuedChatClient.ToolCall(ToolName, new { name = "b", score = 60 }),
            _ => QueuedChatClient.ToolCall(ToolName, new { name = "c", score = 70 }));
        var generator = new ForcedToolGenerator(chat);

        var outcome = await generator.GenerateAsync(Request(ScoreInRange));

        Assert.False(outcome.Succeeded);
        Assert.Equal("generation.schema_violation", outcome.Error!.Code);
        Assert.False(outcome.Error.Retryable);
        Assert.Equal(3, chat.Calls.Count); // initial + 2 retries (MaxRetries = 2)
    }

    [Fact]
    public async Task No_tool_call_is_treated_as_a_schema_violation_and_exhausts_to_ToolError()
    {
        var chat = new QueuedChatClient(
            _ => QueuedChatClient.TextOnly("here is a prose answer"),
            _ => QueuedChatClient.TextOnly("still prose"),
            _ => QueuedChatClient.TextOnly("prose again"));
        var generator = new ForcedToolGenerator(chat);

        var outcome = await generator.GenerateAsync(Request(_ => ValidationResult.Valid));

        Assert.False(outcome.Succeeded);
        Assert.Equal("generation.schema_violation", outcome.Error!.Code);
        Assert.Equal(3, chat.Calls.Count);
    }
}
