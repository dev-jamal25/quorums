namespace Backend.Core.Domain;

/// <summary>
/// The publish surface a request targets (DL-055). One channel-aware <c>IMetaIntegration</c> branches
/// Instagram (media container → poll → media_publish) versus Facebook Page (unpublished photo → feed
/// post) internally; it is never two interfaces. Idempotency keys on <c>(ContentItemId, Channel)</c>,
/// so a content item publishes to each channel as an independent crash-safe unit.
/// <para>Persisted as text on <see cref="PublishRecord"/> (same idiom as <see cref="PublishStatus"/>):
/// the member NAMES are load-bearing — append, never renumber or rename existing values.</para>
/// </summary>
public enum PublishChannel
{
    Instagram,
    FacebookPage,
}
