using Backend.Core.Knowledge;
using Backend.Core.Multitenancy;

namespace Backend.Infrastructure.Jobs;

/// <summary>
/// Hangfire entrypoint for knowledge ingest: binds brand scope, then runs the ingest
/// service (chunk → embed → idempotent upsert). A Hangfire retry re-runs the same
/// deterministic upsert, so it never duplicates chunks.
/// </summary>
/// <remarks>
/// Ingest only. DELETE purges synchronously in the controller (no FK cascade exists, so
/// cleanup must be in-request) — there is intentionally no async purge job entrypoint.
/// </remarks>
public sealed class IngestKnowledgeDocJob
{
    private readonly IBrandScope _scope;
    private readonly IBrandContext _brandContext;
    private readonly IKnowledgeIngestService _ingest;

    public IngestKnowledgeDocJob(IBrandScope scope, IBrandContext brandContext, IKnowledgeIngestService ingest)
    {
        _scope = scope;
        _brandContext = brandContext;
        _ingest = ingest;
    }

    public async Task ExecuteAsync(Guid docId, Guid brandId, CancellationToken cancellationToken = default)
    {
        _brandContext.Bind(brandId);
        await using var handle = await _scope.BeginAsync(cancellationToken).ConfigureAwait(false);
        await _ingest.IngestAsync(docId, cancellationToken).ConfigureAwait(false);
        await handle.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
