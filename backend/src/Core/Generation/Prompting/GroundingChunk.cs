namespace Backend.Core.Generation.Prompting;

/// <summary>
/// One retrieved brand-knowledge chunk as injected into an agent prompt's grounding block
/// (DL-027): a stable provenance <see cref="ChunkId"/> (which the agent echoes into
/// <c>grounding.chunkIdsUsed</c> and which the grounding validator intersects against, R6), the
/// source <see cref="DocType"/>, and the chunk <see cref="Text"/>.
/// </summary>
public sealed record GroundingChunk(string ChunkId, string DocType, string Text);
