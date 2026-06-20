using System.Collections.Concurrent;

namespace Backend.Infrastructure.Integrations.Gemini;

/// <summary>
/// Process-wide map of <c>assetId → in-flight Veo operation name</c> (DL-058). This is the heart of the
/// submit-or-resume idempotency: Veo is asynchronous AND paid, so the operation name is recorded the
/// instant <c>SubmitAsync</c> returns — <b>before any polling</b> — and a node retry resumes by polling
/// it instead of submitting a second (re-billed) job.
/// <para>
/// Registered as a <b>singleton</b> so it survives the <b>transient</b> media-tool/HTTP instances a
/// Hangfire/Polly retry resolves — a direct mirror of <see cref="Meta.LivePublishContextStore"/>, the
/// audit-#1 publish-side fix. The op name does NOT live in <c>RunCheckpoint</c>: that is written only at
/// the gate, AFTER generation, so it cannot carry an in-flight op during the Running phase.
/// </para>
/// <para>
/// Only a true cross-process worker restart mid-generation loses this map (the documented limit — same
/// class as the DL-055 publish cross-process residual, intentionally deferred, not solved here).
/// </para>
/// </summary>
public sealed class VeoOperationStore
{
    private readonly ConcurrentDictionary<Guid, string> _operations = new();

    /// <summary>Record the in-flight operation name keyed by the deterministic asset id (at submit time).</summary>
    public void Set(Guid assetId, string operationName) => _operations[assetId] = operationName;

    /// <summary>Resume an in-flight operation for this asset id; false (and null) if none — submit a new one.</summary>
    public bool TryGet(Guid assetId, out string? operationName) =>
        _operations.TryGetValue(assetId, out operationName);

    /// <summary>Evict the operation once its asset is committed to storage (no re-bill window on a clean retry).</summary>
    public void Remove(Guid assetId) => _operations.TryRemove(assetId, out _);
}
