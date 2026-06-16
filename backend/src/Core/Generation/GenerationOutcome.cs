using System.Diagnostics.CodeAnalysis;
using Backend.Core.Orchestration;

namespace Backend.Core.Generation;

/// <summary>
/// The result of a structured-output generation (DL-028): either a validated typed
/// <see cref="Value"/>, or a <see cref="ToolError"/> returned after the bounded retries are
/// exhausted (DL-027). It is a typed result, never a thrown exception into the graph (DL-022).
/// Construct via the non-generic <see cref="GenerationOutcome"/> factory.
/// </summary>
public sealed class GenerationOutcome<T>
    where T : class
{
    internal GenerationOutcome(T? value, ToolError? error)
    {
        Value = value;
        Error = error;
    }

    /// <summary>The validated output, present when <see cref="Succeeded"/>.</summary>
    public T? Value { get; }

    /// <summary>The structured error, present when generation failed after retries.</summary>
    public ToolError? Error { get; }

    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool Succeeded => Error is null;
}

/// <summary>Factories for <see cref="GenerationOutcome{T}"/> (kept off the generic type per CA1000).</summary>
public static class GenerationOutcome
{
    public static GenerationOutcome<T> Ok<T>(T value)
        where T : class => new(value, null);

    public static GenerationOutcome<T> Fail<T>(ToolError error)
        where T : class => new(null, error);
}
