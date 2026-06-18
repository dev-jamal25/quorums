namespace Backend.Core.Domain;

/// <summary>
/// The engagement-poll handles the Analytics agent reads later (DL-038, Phase 7). Shaped now,
/// populated when publishing goes live. Persisted as an owned value object (two nullable columns)
/// on <see cref="PublishRecord"/>.
/// </summary>
public sealed record EngagementKeys(string MediaId, string? Permalink);
