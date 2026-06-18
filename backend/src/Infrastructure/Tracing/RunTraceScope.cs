namespace Backend.Infrastructure.Tracing;

/// <summary>
/// Ambient per-run trace context, flowed via <see cref="AsyncLocal{T}"/> from a job entry through the
/// agent graph to the LLM client — so a generation emitted by <see cref="LangfuseChatClient"/> attaches
/// to the run's trace instead of being an orphan. Set at EVERY job that makes LLM calls (ExecuteRun and
/// RegenerateRun); ResumeRun/publish makes none. The trace id derives from the run id, so generations
/// (which precede a node's span) share the same trace as the spans without coordination. Disposing clears it.
/// </summary>
public sealed class RunTraceScope : IDisposable
{
    private static readonly AsyncLocal<RunTraceContext?> _current = new();

    private RunTraceScope()
    {
    }

    /// <summary>The current run-trace context, or null outside a run's LLM segment.</summary>
    public static RunTraceContext? Current => _current.Value;

    /// <summary>Binds the run-trace context for the current async flow until the returned scope is disposed.</summary>
    public static RunTraceScope Begin(Guid runId, Guid brandId)
    {
        _current.Value = new RunTraceContext(runId, brandId);
        return new RunTraceScope();
    }

    public void Dispose() => _current.Value = null;
}

/// <summary>The run + brand an LLM generation is attributed to (the Langfuse trace id derives from the run id).</summary>
public sealed record RunTraceContext(Guid RunId, Guid BrandId);
