namespace Backend.Core.Orchestration.Contracts;

public sealed record MediaAssetRef(
    Guid AssetId,
    string StorageKey,
    string Modality,
    string MimeType);
