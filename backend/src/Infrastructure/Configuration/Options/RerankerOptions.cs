using System.ComponentModel.DataAnnotations;

namespace Backend.Infrastructure.Configuration.Options;

/// <summary>
/// Cross-encoder reranker settings (DL-024/025). <see cref="Endpoint"/> is host:port only; the
/// app prepends the scheme at registration. <see cref="Mode"/>: <c>tei</c> (real tei-rerank,
/// bge-reranker-v2-m3 via /rerank) or <c>mock</c> (deterministic, offline). CI uses mock.
/// </summary>
public sealed class RerankerOptions
{
    public const string SectionName = "Reranker";

    [Required(AllowEmptyStrings = false)]
    public string Endpoint { get; init; } = "tei-rerank:80";

    /// <summary><c>tei</c> (real tei-rerank) or <c>mock</c> (deterministic, offline). CI uses mock.</summary>
    public string Mode { get; init; } = "tei";
}
