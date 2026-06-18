using Backend.Core.Domain;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Xunit;

namespace Backend.UnitTests.Orchestration;

/// <summary>
/// The pure resume-decision mapping (DL-039). No I/O: a published result completes; a terminal
/// failure fails immediately (0 retries); a transient failure retries until the final allotted
/// attempt, then fails with the last error — so the run always reaches a terminal state.
/// </summary>
public sealed class PublishResumeDecisionTests
{
    private const int MaxAttempts = 3;

    private static PublishResult Result(PublishStatus status, string? error = null) =>
        new(status, ExternalRef: status == PublishStatus.Published ? "mock://meta/x" : null, Error: error, EngagementKeys: null);

    [Fact]
    public void Published_completes()
    {
        var decision = PublishResumeDecision.Decide(Result(PublishStatus.Published), attempt: 0, MaxAttempts);
        Assert.Equal(PublishResumeOutcome.Complete, decision.Outcome);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Terminal_fails_immediately_at_any_attempt(int attempt)
    {
        var decision = PublishResumeDecision.Decide(Result(PublishStatus.TerminalFailure, "policy rejected"), attempt, MaxAttempts);
        Assert.Equal(PublishResumeOutcome.Fail, decision.Outcome);
        Assert.Equal("policy rejected", decision.FailureReason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Transient_before_the_final_attempt_retries(int attempt)
    {
        var decision = PublishResumeDecision.Decide(Result(PublishStatus.TransientFailure, "5xx"), attempt, MaxAttempts);
        Assert.Equal(PublishResumeOutcome.Retry, decision.Outcome);
    }

    [Fact]
    public void Transient_on_the_final_attempt_fails_with_the_last_error()
    {
        var decision = PublishResumeDecision.Decide(Result(PublishStatus.TransientFailure, "still 5xx"), attempt: MaxAttempts, MaxAttempts);
        Assert.Equal(PublishResumeOutcome.Fail, decision.Outcome);
        Assert.Equal("still 5xx", decision.FailureReason);
    }

    [Fact]
    public void Missing_result_is_treated_transient_then_fails_when_exhausted()
    {
        Assert.Equal(PublishResumeOutcome.Retry, PublishResumeDecision.Decide(null, attempt: 0, MaxAttempts).Outcome);
        Assert.Equal(PublishResumeOutcome.Fail, PublishResumeDecision.Decide(null, attempt: MaxAttempts, MaxAttempts).Outcome);
    }
}
