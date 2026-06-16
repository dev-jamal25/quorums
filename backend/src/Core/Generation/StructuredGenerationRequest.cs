namespace Backend.Core.Generation;

/// <summary>
/// One forced-tool structured-output request (DL-028). The <see cref="Prompt"/> is the assembled
/// 5-part skeleton (DL-027); the tool's input schema is derived from <typeparamref name="T"/> by
/// the generator (record-first, R4). <see cref="Validate"/> runs on the deserialized output and,
/// on failure, supplies the specific error fed back into the retry prompt. <see cref="ModelId"/>
/// is config-bound by the caller — never a literal (DL-029). The bounded retry budget is
/// <see cref="MaxRetries"/> (default 2, DL-027/028).
/// </summary>
public sealed record StructuredGenerationRequest<T>(
    string Prompt,
    string ToolName,
    string ToolDescription,
    string ModelId,
    Func<T, ValidationResult> Validate)
    where T : class
{
    /// <summary>Output-token ceiling for the call (config-bound by the caller).</summary>
    public int MaxOutputTokens { get; init; } = 1024;

    /// <summary>The bounded retry budget after the first attempt (DL-027/028 — default 2).</summary>
    public int MaxRetries { get; init; } = 2;
}
