using Backend.Core.Generation.Prompting;
using Xunit;

namespace Backend.UnitTests.Generation;

/// <summary>
/// The shared 5-part prompt skeleton (DL-027 §1): every part is present in order, the grounding
/// block tags chunks with provenance ids, an empty block instructs the ungrounded path (DL-022),
/// and the relevant PlatformConstraints are injected (the inform half of DL-030).
/// </summary>
public sealed class PromptSkeletonTests
{
    private static AgentPromptParts Parts(IReadOnlyList<GroundingChunk> chunks, IReadOnlyList<string> constraints) =>
        new(
            RoleMandate: "You own what to say.",
            GroundingChunks: chunks,
            InputSliceJson: "{\"brief\":\"launch\"}",
            Task: "Produce three candidate strategies.",
            Constraints: constraints,
            ToolName: "record_strategies");

    [Fact]
    public void Build_emits_the_five_parts_in_order()
    {
        var chunks = new[] { new GroundingChunk("pb_mission_03", "brand_playbook", "Make single-origin approachable.") };

        var prompt = PromptSkeleton.Build(Parts(chunks, ["<= 30 hashtags"]));

        int role = prompt.IndexOf("[role]", StringComparison.Ordinal);
        int grounding = prompt.IndexOf("[grounding]", StringComparison.Ordinal);
        int input = prompt.IndexOf("[input]", StringComparison.Ordinal);
        int task = prompt.IndexOf("[task]", StringComparison.Ordinal);
        int output = prompt.IndexOf("[output]", StringComparison.Ordinal);

        Assert.True(role >= 0 && role < grounding && grounding < input && input < task && task < output);
        Assert.Contains("record_strategies", prompt, StringComparison.Ordinal);
        Assert.Contains("<= 30 hashtags", prompt, StringComparison.Ordinal); // constraint injected (inform)
    }

    [Fact]
    public void Grounding_block_tags_each_chunk_with_its_provenance_id_and_docType()
    {
        var chunks = new[]
        {
            new GroundingChunk("pb_mission_03", "brand_playbook", "mission text"),
            new GroundingChunk("prod_017", "product", "product text"),
        };

        var block = PromptSkeleton.FormatGroundingBlock(chunks);

        Assert.Contains("{chunkId=pb_mission_03 | docType=brand_playbook}", block, StringComparison.Ordinal);
        Assert.Contains("{chunkId=prod_017 | docType=product}", block, StringComparison.Ordinal);
        Assert.Equal(["pb_mission_03", "prod_017"], PromptSkeleton.ProvenanceIds(chunks));
    }

    [Fact]
    public void Empty_grounding_block_instructs_the_ungrounded_path()
    {
        var block = PromptSkeleton.FormatGroundingBlock([]);

        Assert.Contains("grounding.grounded = false", block, StringComparison.Ordinal);
        Assert.Empty(PromptSkeleton.ProvenanceIds([]));
    }
}
