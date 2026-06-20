namespace Backend.Core.Orchestration.Contracts;

/// <summary>
/// The single generated asset on <c>RunState.Media</c> (one per content item — no carousel, DL-058).
/// <see cref="DurationSec"/> is the clip length for a video asset (null for an image).
/// </summary>
public sealed record MediaAssetRef(
    Guid AssetId,
    string StorageKey,
    string Modality,
    string MimeType,
    int? DurationSec = null);
