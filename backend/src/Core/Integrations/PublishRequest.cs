namespace Backend.Core.Integrations;

/// <summary>
/// The approved content handed to <see cref="IMetaIntegration"/> at publish time, shaped for the
/// real Instagram content-publish (DL-038). <see cref="ContentItemId"/> is the idempotency key: the
/// publish-execution component keys its <c>PublishRecord</c> guard on it so a retried publish never
/// creates a second post (DL-022, DL-039). <see cref="AccessToken"/> is the per-brand Vault-decrypted
/// token — never logged (DL-011); the mock ignores it.
/// </summary>
public sealed record PublishRequest(
    Guid ContentItemId,
    PostSurface Surface,
    string MediaUrl,
    string Caption,
    IReadOnlyList<string> Hashtags,
    string AccessToken);
