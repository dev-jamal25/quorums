using System.Text.Json.Serialization;

namespace Backend.Core.Integrations;

/// <summary>
/// The Instagram publishing surface (DL-038). Modality-aware: an image or video maps to a
/// feed/reel/story container, and the per-surface <c>PlatformConstraints</c> (DL-030) follow from
/// it. The mock ignores the distinction; <c>LiveMetaIntegration</c> would pick the Graph API
/// container type from it.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PostSurface
{
    FeedImage,
    FeedVideo,
    Reel,
    Story,
}
