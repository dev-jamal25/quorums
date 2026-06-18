using Backend.Core.Domain;
using Backend.Core.Evaluation;
using Backend.Core.Knowledge;
using Backend.Infrastructure.Generation;
using Backend.IntegrationTests.Support;
using Xunit;

namespace Backend.IntegrationTests.Eval;

/// <summary>
/// Audit probe A1 — recording-double per-node provenance attribution. Documented <b>Skip</b> pending a
/// frozen DL-054: the generation pipeline computes each node's injected provenance ids
/// (<c>PromptSkeleton.ProvenanceIds</c>) only to feed the inline <c>GroundingValidator.Reconcile</c> and
/// then discards them — they are never written to <c>RunState</c> or the trace, and the only ambient
/// scope (<c>RunTraceScope</c>) is per-run, not per-node. So on a real run there is no per-node injected
/// source: <c>InjectedChunkIdsByNode</c> would be empty and the grounding-honesty metric is not
/// independently verifiable end-to-end. The test-support double papers over this by unioning all calls
/// (<c>TestGeneration.OffState</c> assigns the union to every node), which is exactly what this probe
/// catches. Re-enabling it requires the DL-054 per-node provenance seam — do NOT fix without review.
/// </summary>
[Trait("Category", "Eval")]
public sealed class AuditProbeTests
{
    [Fact(Skip = "AUDIT A1 — CASE 2 (production per-node provenance gap), pending DL-054. The recording " +
        "double unions all retrieval calls and OffState assigns that union to every node, so a node that " +
        "never retrieved (Copywriting) wrongly receives another node's ids. Observed RED: 'Assert.Empty() " +
        "Failure: Collection was not empty'. Real fix needs a per-node injected-provenance seam shared by " +
        "the real path and the double; production lacks it today. Do not fix without a frozen DL.")]
    public async Task A1_recording_double_attributes_injected_ids_to_the_correct_node()
    {
        var a = new RetrievedChunk(Guid.NewGuid(), Guid.NewGuid(), "A", DocType.BrandPlaybook, null, 0.9);
        var b = new RetrievedChunk(Guid.NewGuid(), Guid.NewGuid(), "B", DocType.Product, null, 0.9);
        var recording = new RecordingRetrievalService(new SequencedRetrieval([a], [b]));

        // ContentStrategist retrieves twice (call order ≠ node order); Copywriting retrieves nothing.
        await recording.Retrieve("q", Guid.NewGuid(), DocType.BrandPlaybook, 3);
        await recording.Retrieve("q", Guid.NewGuid(), DocType.Product, 3);

        var chat = new CountingChatClient(new DeterministicGenerationChatClient());
        var (injected, _) = TestGeneration.OffState(recording, chat);

        // Correct attribution: Copywriting (which never retrieved) must have an EMPTY injected set.
        Assert.Empty(injected[SystemOutput.Nodes.Copywriting]);
    }

    private sealed class SequencedRetrieval : IRetrievalService
    {
        private readonly Queue<IReadOnlyList<RetrievedChunk>> _responses;

        public SequencedRetrieval(params IReadOnlyList<RetrievedChunk>[] responses) =>
            _responses = new Queue<IReadOnlyList<RetrievedChunk>>(responses);

        public Task<RetrievalResult> Retrieve(string query, Guid brandId, DocType? docType, int k)
        {
            IReadOnlyList<RetrievedChunk> chunks = _responses.Count > 0 ? _responses.Dequeue() : Array.Empty<RetrievedChunk>();
            return Task.FromResult(new RetrievalResult(chunks, chunks.Count > 0));
        }
    }
}
