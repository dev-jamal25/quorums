using Backend.Core.Domain;
using Backend.Core.Orchestration.Contracts;

namespace Backend.Core.Orchestration;

/// <summary>
/// The durable shared-state object threaded through the agent graph and persisted to
/// <c>RunCheckpoint</c> JSON (no migration — fields are append-only). The Supervisor is the sole
/// writer of <c>Phase</c>, <c>Draft</c>, and <c>Budget</c> (DL-020); each agent writes only its
/// declared slice and appends its <c>IncurredCosts</c>.
/// <list type="bullet">
///   <item><c>TargetSurface</c> — the PlatformConstraints key (e.g. <c>instagram_feed</c>): the
///   aspect-ratio stamp + Copywriting/Media constraints read it.</item>
///   <item><c>ContentPillars</c> — the brand's pillars, loaded once under RLS: the Strategist's
///   validation contract (R7).</item>
///   <item><c>Candidates</c> — the Strategist's N=3 candidates, handed to the selection step.</item>
///   <item><c>IncurredCosts</c> — per-node cost reports, reconciled into <c>Budget</c> at the join (R3).</item>
///   <item><c>FatalError</c> — set by a fatally-failing node (Strategist/CD/selection exhaustion,
///   global-ceiling, Gemini-fail): the job fails the run; downstream nodes short-circuit (DL-022/023).</item>
///   <item><c>Modality</c> — <c>image</c> (default) or <c>video</c>: the CD stamps it onto the brief and
///   the Media node branches the budget + generation on it (DL-058).</item>
///   <item><c>VideoSource</c> — for a video run, image-seed (default) vs text-to-video (DL-058).</item>
/// </list>
/// </summary>
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
    TraceRefs Trace,
    string TargetSurface,
    IReadOnlyList<string> ContentPillars,
    StrategyCandidates? Candidates,
    List<NodeCost> IncurredCosts,
    ToolError? FatalError,
    string Modality = "image",
    VideoSource VideoSource = VideoSource.ImageSeed);
