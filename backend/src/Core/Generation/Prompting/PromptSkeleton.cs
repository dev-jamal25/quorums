using System.Text;

namespace Backend.Core.Generation.Prompting;

/// <summary>
/// Assembles the shared 5-part agent prompt skeleton (DL-027 §1). Pure and deterministic — every
/// agent prompt is an instantiation of these five parts in order, never a bespoke structure. The
/// grounding block tags each chunk with its provenance id so the agent can cite it; an empty block
/// instructs the agent to set <c>grounding.grounded = false</c> and proceed ungrounded (DL-022).
/// </summary>
public static class PromptSkeleton
{
    /// <summary>Builds the full prompt from its five parts.</summary>
    public static string Build(AgentPromptParts parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var builder = new StringBuilder();

        // 1. role / mandate
        builder.Append("[role]\n").Append(parts.RoleMandate.Trim()).Append("\n[/role]\n\n");

        // 2. brand grounding block (with provenance ids)
        builder.Append(FormatGroundingBlock(parts.GroundingChunks)).Append("\n\n");

        // 3. input slice (declared upstream RunState outputs, as JSON)
        builder.Append("[input]\n").Append(parts.InputSliceJson.Trim()).Append("\n[/input]\n\n");

        // 4. task + relevant PlatformConstraints (the inform half of DL-030)
        builder.Append("[task]\n").Append(parts.Task.Trim());
        if (parts.Constraints.Count > 0)
        {
            builder.Append("\n\nConstraints (these are checked deterministically after you respond):");
            foreach (var constraint in parts.Constraints)
            {
                builder.Append("\n- ").Append(constraint);
            }
        }

        builder.Append("\n[/task]\n\n");

        // 5. output-schema instruction (forced tool)
        builder.Append("[output]\n")
            .Append("Respond by calling the ").Append(parts.ToolName)
            .Append(" tool exactly once, and nothing else. Ground your output in the chunks above: put the ")
            .Append("ids of the chunks you actually used in grounding.chunkIdsUsed, and set grounding.confidence ")
            .Append("to reflect how well they supported the output.")
            .Append("\n[/output]");

        return builder.ToString();
    }

    /// <summary>
    /// Formats the grounding block: one line per chunk tagged with its provenance id and docType.
    /// An empty set yields an explicit empty-block instruction (degraded path, not an error).
    /// </summary>
    public static string FormatGroundingBlock(IReadOnlyList<GroundingChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        if (chunks.Count == 0)
        {
            return "[grounding]\n(no brand knowledge was retrieved — set grounding.grounded = false " +
                   "and proceed ungrounded)\n[/grounding]";
        }

        var builder = new StringBuilder("[grounding]\n");
        foreach (var chunk in chunks)
        {
            builder.Append("{chunkId=").Append(chunk.ChunkId)
                .Append(" | docType=").Append(chunk.DocType).Append("} ")
                .Append(chunk.Text.Trim()).Append('\n');
        }

        builder.Append("[/grounding]");
        return builder.ToString();
    }

    /// <summary>The provenance ids injected into the prompt — the set the grounding validator intersects against (R6).</summary>
    public static IReadOnlyList<string> ProvenanceIds(IReadOnlyList<GroundingChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        return chunks.Select(chunk => chunk.ChunkId).ToList();
    }
}
