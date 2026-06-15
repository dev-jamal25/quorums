namespace Backend.Core.Generation.Prompting;

/// <summary>
/// The five parts every LLM agent prompt (and the Supervisor selection call) is assembled from
/// (DL-027 §1): role/mandate, the brand-grounding block (with provenance ids), the upstream
/// RunState input slice (JSON, the declared slice only), the task plus the relevant
/// <c>PlatformConstraints</c> (the <em>inform</em> half of DL-030), and the forced-tool schema
/// instruction. The per-agent role text and which constraints apply are supplied by the caller;
/// this is the shared skeleton, not a bespoke per-agent prompt.
/// </summary>
public sealed record AgentPromptParts(
    string RoleMandate,
    IReadOnlyList<GroundingChunk> GroundingChunks,
    string InputSliceJson,
    string Task,
    IReadOnlyList<string> Constraints,
    string ToolName);
