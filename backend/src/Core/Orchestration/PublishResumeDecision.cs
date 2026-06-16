using Backend.Core.Domain;
using Backend.Core.Orchestration.Contracts;

namespace Backend.Core.Orchestration;

public enum PublishResumeOutcome
{
    Complete,
    Fail,
    Retry,
}

/// <summary>
/// The pure mapping from a publish <see cref="PublishResult"/> + the current Hangfire attempt to the
/// resume outcome (DL-039). No I/O — directly unit-testable. <c>ResumeRun</c> is the single caller:
/// <see cref="PublishResumeOutcome.Complete"/> → <c>Done</c>; <see cref="PublishResumeOutcome.Fail"/>
/// → <c>Failed</c> (return normally, surface the reason); <see cref="PublishResumeOutcome.Retry"/> →
/// throw to trigger the bounded automatic retry. The run therefore ALWAYS reaches a terminal state.
/// </summary>
public readonly record struct PublishResumeDecision(PublishResumeOutcome Outcome, string? FailureReason)
{
    public static PublishResumeDecision Complete { get; } = new(PublishResumeOutcome.Complete, null);

    public static PublishResumeDecision Retry { get; } = new(PublishResumeOutcome.Retry, null);

    public static PublishResumeDecision Fail(string? reason) => new(PublishResumeOutcome.Fail, reason);

    /// <param name="result">The node-recorded publish result (null = the node recorded none — treat as transient).</param>
    /// <param name="attempt">The current attempt index (Hangfire <c>RetryCount</c>: 0 on first run).</param>
    /// <param name="maxAttempts">The retry budget; the final allotted attempt is <paramref name="attempt"/> == this.</param>
    public static PublishResumeDecision Decide(PublishResult? result, int attempt, int maxAttempts)
    {
        if (result is { Status: PublishStatus.Published })
        {
            return Complete;
        }

        // Terminal never goes through the retry counter — fail on first sight, 0 retries.
        if (result is { Status: PublishStatus.TerminalFailure })
        {
            return Fail(result.Error);
        }

        // TransientFailure (or a missing result): retry until the final attempt, then fail with the
        // last error — never linger in Publishing.
        var reason = result?.Error ?? "Publish did not complete.";
        return attempt >= maxAttempts ? Fail(reason) : Retry;
    }
}
