using Backend.Core.Orchestration.Contracts;

namespace Backend.Core.Generation.Validation;

/// <summary>
/// Grounding honesty (DL-028, DL-034 R6). The model's self-reported <see cref="Grounding.Grounded"/>
/// is never trusted: this intersects the model's claimed <see cref="Grounding.ChunkIdsUsed"/> with
/// the provenance ids actually injected into that prompt, keeps the intersection, and <b>derives</b>
/// <c>Grounded = (intersection non-empty)</c>. <see cref="Grounding.Confidence"/> may stay model-set,
/// reported alongside the validated id count. Empty retrieval → empty intersection → <c>Grounded =
/// false</c>, and the agent proceeds ungrounded (DL-022). This is a reconciliation transform, not a
/// retry trigger.
/// </summary>
public static class GroundingValidator
{
    public static Grounding Reconcile(Grounding claimed, IReadOnlyList<string> injectedProvenanceIds)
    {
        ArgumentNullException.ThrowIfNull(claimed);

        var injected = new HashSet<string>(
            injectedProvenanceIds ?? [], StringComparer.Ordinal);

        var kept = (claimed.ChunkIdsUsed ?? [])
            .Where(injected.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return claimed with
        {
            ChunkIdsUsed = kept,
            Grounded = kept.Count > 0,
        };
    }
}
