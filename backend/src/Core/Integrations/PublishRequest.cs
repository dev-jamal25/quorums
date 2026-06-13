namespace Backend.Core.Integrations;

/// <summary>
/// The approved content handed to <see cref="IMetaIntegration"/> at publish time.
/// <see cref="ContentItemId"/> is the idempotency key: a retried publish for the same
/// content must not create a second external post (DL-022).
/// </summary>
public sealed record PublishRequest(
    Guid BrandId,
    Guid ContentItemId,
    string Caption,
    string? MediaStorageKey);
