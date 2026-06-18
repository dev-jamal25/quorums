using Backend.Core.Domain;
using Backend.Core.Integrations;
using Backend.Core.Multitenancy;
using Backend.Core.Orchestration.Contracts;
using Backend.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Backend.Infrastructure.Integrations.Meta;

/// <summary>
/// Runs the robust, idempotent two-step Instagram publish against <see cref="IMetaIntegration"/>
/// (DL-038, DL-039). The durable guard is the persisted <see cref="PublishRecord"/> keyed on
/// <c>ContentItemId</c>; the container <see cref="PublishRecord.CreationId"/> is persisted (and
/// COMMITTED) immediately after create and BEFORE publish, so a crash-and-retry never double-posts:
///   <list type="bullet">
///     <item>no record → create, persist CreationId, poll, publish, finalize;</item>
///     <item>record with CreationId and no ExternalRef → do NOT re-create; re-publish the same
///     container (Meta dedups) and finalize;</item>
///     <item>record finalized → skip, return the existing ExternalRef.</item>
///   </list>
/// A crash in the create→persist-CreationId window leaves only an orphan unpublished container
/// (harmless). Failures are classified from the typed step results, never by exception-sniffing.
/// <para>Each persistence step is its OWN committed brand-scope unit (<see cref="IBrandScope"/>): the
/// brand GUC is transaction-local, so a single long transaction would (a) lose the CreationId on a
/// mid-publish crash and (b) hold a DB transaction open across network calls. The coordinator
/// therefore owns its transaction boundaries and never publishes inside one. The Meta calls run
/// between units, transaction-free. DI registration + graph-node delegation land in Slice 4.</para>
/// </summary>
public sealed class PublishCoordinator
{
    private readonly AppDbContext _db;
    private readonly IBrandScope _scope;
    private readonly IMetaIntegration _meta;

    public PublishCoordinator(AppDbContext db, IBrandScope scope, IMetaIntegration meta)
    {
        _db = db;
        _scope = scope;
        _meta = meta;
    }

    public async Task<PublishResult> PublishAsync(
        PublishRequest request, Guid runId, Guid brandId, CancellationToken cancellationToken = default)
    {
        // 1) Inspect the durable state (committed read unit).
        var snapshot = await ReadSnapshotAsync(request.ContentItemId, cancellationToken).ConfigureAwait(false);
        if (snapshot.ExternalRef is not null)
        {
            // Finalized already → idempotent skip, no publish.
            return new PublishResult(PublishStatus.Published, snapshot.ExternalRef, null, snapshot.EngagementKeys);
        }

        // 2) Ensure a container exists with a COMMITTED CreationId (the crash-safety hinge, DL-039).
        string creationId;
        if (snapshot.CreationId is { } existing)
        {
            creationId = existing;
            await BumpAttemptAsync(request.ContentItemId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var created = await _meta.CreateContainerAsync(request, cancellationToken).ConfigureAwait(false);
            if (created.CreationId is null)
            {
                // Create failed → no CreationId to persist; a retry simply re-creates.
                var failure = created.Failure ?? PublishStatus.TransientFailure;
                await UpsertAsync(request, runId, brandId, null, failure, null, null, cancellationToken).ConfigureAwait(false);
                return new PublishResult(failure, null, created.Error, null);
            }

            creationId = created.CreationId;
            await UpsertAsync(request, runId, brandId, creationId, PublishStatus.TransientFailure, null, null, cancellationToken).ConfigureAwait(false);
        }

        // 3) Poll until processed.
        var status = await _meta.PollContainerAsync(creationId, cancellationToken).ConfigureAwait(false);
        if (!status.Processed)
        {
            var failure = status.Failure ?? PublishStatus.TransientFailure;
            await UpsertAsync(request, runId, brandId, creationId, failure, null, null, cancellationToken).ConfigureAwait(false);
            return new PublishResult(failure, null, status.Error, null);
        }

        // 4) Publish (idempotent on creationId). A crash after this returns but before finalize leaves
        //    the committed CreationId, so the retry re-publishes the SAME container (deduped).
        var result = await _meta.PublishContainerAsync(creationId, cancellationToken).ConfigureAwait(false);
        if (result.Status != PublishStatus.Published)
        {
            await UpsertAsync(request, runId, brandId, creationId, result.Status, null, null, cancellationToken).ConfigureAwait(false);
            return result;
        }

        // 5) Finalize: record the published media id — closes the idempotency window.
        await UpsertAsync(request, runId, brandId, creationId, PublishStatus.Published, result.ExternalRef, result.EngagementKeys, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<(string? CreationId, string? ExternalRef, EngagementKeys? EngagementKeys)> ReadSnapshotAsync(
        Guid contentItemId, CancellationToken cancellationToken)
    {
        await using var handle = await _scope.BeginAsync(cancellationToken).ConfigureAwait(false);
        var record = await _db.PublishRecords.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ContentItemId == contentItemId, cancellationToken)
            .ConfigureAwait(false);
        await handle.CompleteAsync(cancellationToken).ConfigureAwait(false);
        return (record?.CreationId, record?.ExternalRef, record?.EngagementKeys);
    }

    private async Task BumpAttemptAsync(Guid contentItemId, CancellationToken cancellationToken)
    {
        await using var handle = await _scope.BeginAsync(cancellationToken).ConfigureAwait(false);
        var record = await _db.PublishRecords
            .FirstOrDefaultAsync(r => r.ContentItemId == contentItemId, cancellationToken)
            .ConfigureAwait(false);
        if (record is not null)
        {
            record.AttemptCount += 1;
            record.OccurredAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await handle.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Create-or-update the brand-scoped <see cref="PublishRecord"/> in its own committed unit.</summary>
    private async Task UpsertAsync(
        PublishRequest request, Guid runId, Guid brandId, string? creationId, PublishStatus status,
        string? externalRef, EngagementKeys? engagementKeys, CancellationToken cancellationToken)
    {
        await using var handle = await _scope.BeginAsync(cancellationToken).ConfigureAwait(false);

        var record = await _db.PublishRecords
            .FirstOrDefaultAsync(r => r.ContentItemId == request.ContentItemId, cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            _db.PublishRecords.Add(new PublishRecord
            {
                Id = Guid.NewGuid(),
                BrandId = brandId,
                AgentRunId = runId,
                ContentItemId = request.ContentItemId,
                CreationId = creationId,
                Status = status,
                ExternalRef = externalRef,
                EngagementKeys = engagementKeys,
                AttemptCount = 1,
                OccurredAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            if (creationId is not null)
            {
                record.CreationId = creationId;
            }

            record.Status = status;
            if (externalRef is not null)
            {
                record.ExternalRef = externalRef;
            }

            if (engagementKeys is not null)
            {
                record.EngagementKeys = engagementKeys;
            }

            record.OccurredAt = DateTimeOffset.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await handle.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }
}
