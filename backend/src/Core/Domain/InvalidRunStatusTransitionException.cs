namespace Backend.Core.Domain;

/// <summary>
/// Thrown when code attempts a <see cref="RunStatus"/> change that <see cref="RunStatusTransition"/>
/// does not permit. A programming/state error — never expected on a legal run flow.
/// </summary>
public sealed class InvalidRunStatusTransitionException : InvalidOperationException
{
    public InvalidRunStatusTransitionException(RunStatus from, RunStatus to)
        : base($"Illegal run status transition: {from} -> {to}.")
    {
        From = from;
        To = to;
    }

    public RunStatus From { get; }

    public RunStatus To { get; }
}
