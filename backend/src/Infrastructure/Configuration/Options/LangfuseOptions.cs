namespace Backend.Infrastructure.Configuration.Options;

/// <summary>
/// Langfuse tracing settings. All optional: empty keys select the no-op local
/// recorder (tracing degrades, the run never fails). When all three are present the
/// LangfuseTrace recorder posts spans best-effort. Keys are secrets — supplied via
/// env / Vault KV, never committed.
/// </summary>
public sealed class LangfuseOptions
{
    public const string SectionName = "Langfuse";

    public string? BaseUrl { get; init; }

    public string? PublicKey { get; init; }

    public string? SecretKey { get; init; }

    /// <summary>True only when every field needed to reach Langfuse is present.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(PublicKey)
        && !string.IsNullOrWhiteSpace(SecretKey);
}
