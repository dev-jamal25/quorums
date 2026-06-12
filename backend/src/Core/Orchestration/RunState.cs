using Backend.Core.Domain;
using Backend.Core.Orchestration.Contracts;

namespace Backend.Core.Orchestration;

public sealed record RunState(
    Guid RunId,
    Guid BrandId,
    GraphPhase Phase,
    ContentStrategy? Strategy,
    CreativeDirection? Creative,
    Caption? Caption,
    MediaAssetRef? Media,
    ContentItemDraft? Draft,
    ApprovalDecision? Approval,
    PublishResult? Publish,
    Budget Budget,
    List<ToolError> Errors,
    TraceRefs Trace);
