namespace Backend.Core.Orchestration.Contracts;

public sealed record ContentItemDraft(
    Caption CaptionRef,
    MediaAssetRef? MediaRef,
    Guid BrandId,
    string Status);
