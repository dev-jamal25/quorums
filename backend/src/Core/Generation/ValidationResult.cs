namespace Backend.Core.Generation;

/// <summary>
/// The outcome of validating a deserialized agent output on receipt (DL-027/028). A failure
/// carries the <b>specific</b> error message so the structured-output loop can feed it back into
/// the retry prompt (e.g. "hashtags=34, limit=30" / "pillar 'Sustainability' not in [Origin, Craft,
/// Ritual]") rather than re-rolling blind. The only two triggers that produce an
/// <see cref="Invalid"/> are a schema violation and a PlatformConstraints violation (DL-027).
/// </summary>
public sealed record ValidationResult(bool IsValid, string? Error)
{
    /// <summary>A passing validation.</summary>
    public static readonly ValidationResult Valid = new(true, null);

    /// <summary>A failing validation carrying the concrete, feed-back-able error.</summary>
    public static ValidationResult Invalid(string error) => new(false, error);
}
