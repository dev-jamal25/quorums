using System.ComponentModel.DataAnnotations;

namespace Backend.Infrastructure.Configuration.Options;

/// <summary>
/// MinIO object-storage settings. Access/secret keys are secrets (Vault KV /
/// environment); endpoint and bucket are non-secret config.
/// </summary>
public sealed class MinioOptions
{
    public const string SectionName = "Minio";

    [Required(AllowEmptyStrings = false)]
    public string Endpoint { get; init; } = default!;

    [Required(AllowEmptyStrings = false)]
    public string AccessKey { get; init; } = default!;

    [Required(AllowEmptyStrings = false)]
    public string SecretKey { get; init; } = default!;

    [Required(AllowEmptyStrings = false)]
    public string Bucket { get; init; } = default!;
}
