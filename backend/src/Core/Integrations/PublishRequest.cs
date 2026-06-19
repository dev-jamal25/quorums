using Backend.Core.Domain;

namespace Backend.Core.Integrations;

/// <summary>
/// The approved content handed to <see cref="IMetaIntegration"/> at publish time, shaped for the
/// real Instagram + Facebook Page content-publish (DL-038, DL-055). The idempotency key is
/// <c>(ContentItemId, Channel)</c>: the publish-execution component keys its <c>PublishRecord</c> guard
/// on the pair so a retried publish never creates a second post per channel (DL-022, DL-039).
/// <see cref="TargetId"/> (IG Business Account id / Page id) is part of the contract shape but is NOT
/// consumed by the mock; Slice 2 resolves it from <c>BrandMetaConnection</c>. <see cref="AccessToken"/>
/// is the per-brand Vault-decrypted token — never logged (DL-011); the mock ignores it.
/// </summary>
public sealed record PublishRequest(
    Guid ContentItemId,
    PublishChannel Channel,
    string TargetId,
    PostSurface Surface,
    string MediaUrl,
    string Caption,
    IReadOnlyList<string> Hashtags,
    string AccessToken);
