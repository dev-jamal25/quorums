using System.Text.Json;
using Backend.Core.Orchestration;

namespace Backend.Infrastructure.Orchestration.Maf.Nodes;

/// <summary>
/// Durable per-node grounding provenance (DL-054). Each grounding executor (Content Strategist, Creative
/// Director, Copywriting) records — <b>before</b> <c>GroundingValidator.Reconcile</c> overwrites
/// <c>chunkIdsUsed</c> — the <b>raw</b> model-claimed chunk ids and the injected chunk ids for its node,
/// into a dedicated span <see cref="TraceSpan.Detail"/> on the always-on durable trace seam
/// (<see cref="ITrace.RecordAsync"/>, which the local recorder persists even when Langfuse is absent).
/// This is what makes grounding honesty independently auditable end-to-end: the reconciled
/// <c>chunkIdsUsed</c> is ⊆ injected by construction, so only the raw claimed set can support an honest
/// <c>claimed ⊆ injected</c> audit. Provenance rides in the trace only — no <see cref="RunState"/> or
/// agent-output contract change (DL-020).
/// </summary>
internal static class GroundingProvenance
{
    /// <summary>The span <c>tool</c> marker that identifies a grounding-provenance span on the trace.</summary>
    public const string Tool = "grounding.provenance";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>The <see cref="TraceSpan.Detail"/> payload shape — raw claimed + injected chunk ids.</summary>
    public sealed record Detail(IReadOnlyList<string> ClaimedChunkIds, IReadOnlyList<string> InjectedChunkIds);

    public static Task<TraceRefs> RecordAsync(
        ITrace trace,
        TraceRefs current,
        Guid runId,
        Guid brandId,
        string node,
        IReadOnlyList<string> claimedChunkIds,
        IReadOnlyList<string> injectedChunkIds,
        CancellationToken cancellationToken)
    {
        var at = DateTimeOffset.UtcNow;
        var detail = JsonSerializer.Serialize(new Detail(claimedChunkIds, injectedChunkIds), _json);
        return trace.RecordAsync(current, runId, brandId, node, Tool, "ok", at, at, null, detail, cancellationToken);
    }

    /// <summary>Parses a provenance span's <see cref="TraceSpan.Detail"/>, or null if it is not one.</summary>
    public static Detail? TryParse(TraceSpan span)
    {
        if (!string.Equals(span.Tool, Tool, StringComparison.Ordinal) || span.Detail is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Detail>(span.Detail, _json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
