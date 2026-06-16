namespace Backend.Infrastructure.Jobs;

/// <summary>
/// Raised by <see cref="ResumeRunJob"/> (NOT inside the graph) when a publish returned a classified
/// transient failure and the retry budget is not yet exhausted. It escapes the job so Hangfire's
/// <c>AutomaticRetry</c> re-runs <c>ResumeRun</c>; the coordinator then re-enters idempotently on the
/// persisted <c>CreationId</c>, so retries never double-post (DL-039).
/// </summary>
public sealed class TransientPublishException : Exception
{
    public TransientPublishException(string message)
        : base(message)
    {
    }
}
