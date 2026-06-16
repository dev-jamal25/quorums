using Backend.Core.Domain;
using Backend.Core.Integrations;
using Backend.Core.Orchestration.Contracts;
using Backend.Infrastructure.Integrations.Meta;
using Backend.IntegrationTests.Durability;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Backend.IntegrationTests.Publishing;

/// <summary>
/// The Slice-2 acceptance gate for the robust two-step publish (DL-038, DL-039). Drives
/// <see cref="PublishCoordinator"/> against the crash-modeling <see cref="MockMetaIntegration"/> over
/// a real RLS-bound Postgres (the durability fixture), proving the CreationId idempotency: a crash in
/// either window followed by a retry yields exactly one published media; failures are classified from
/// the typed result; a finalized record short-circuits with no publish.
/// </summary>
[Trait("Category", "Publish")]
[Collection("Durability")]
public sealed class PublishCoordinatorTests
{
    private readonly DurabilityFixture _fixture;

    public PublishCoordinatorTests(DurabilityFixture fixture) => _fixture = fixture;

    private static PublishRequest Request(Guid contentItemId) =>
        new(contentItemId, PostSurface.FeedImage, "brands/a/assets/x.png", "hook\n\nbody", ["#a"], AccessToken: "token");

    [Fact]
    public async Task Crash_after_publish_then_retry_yields_exactly_one_published_media()
    {
        var contentItemId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var mock = new MockMetaIntegration { CrashAfterPublishOnce = true };

        // Attempt 1 (its own job/context): publishes on the mock, then crashes before recording.
        var (db1, scope1) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (db1)
        {
            var coordinator = new PublishCoordinator(db1, scope1, mock);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.PublishAsync(Request(contentItemId), runId, _fixture.BrandA));
        }

        // Attempt 2 (the Hangfire retry): recovers via the committed CreationId + re-publish dedup.
        PublishResult result;
        var (db2, scope2) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (db2)
        {
            var coordinator = new PublishCoordinator(db2, scope2, mock);
            result = await coordinator.PublishAsync(Request(contentItemId), runId, _fixture.BrandA);
        }

        Assert.Equal(PublishStatus.Published, result.Status);
        Assert.StartsWith("mock://meta/", result.ExternalRef!);
        Assert.Equal(1, mock.PublishedMediaCount); // exactly one post despite the crash + retry

        var record = await ReadRecordAsync(contentItemId);
        Assert.NotNull(record);
        Assert.Equal(PublishStatus.Published, record!.Status);
        Assert.Equal(result.ExternalRef, record.ExternalRef);
    }

    [Fact]
    public async Task Crash_after_create_before_persist_then_retry_yields_exactly_one_published_media()
    {
        var contentItemId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var mock = new MockMetaIntegration { CrashAfterCreateOnce = true };

        var (db1, scope1) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (db1)
        {
            var coordinator = new PublishCoordinator(db1, scope1, mock);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => coordinator.PublishAsync(Request(contentItemId), runId, _fixture.BrandA));
        }

        PublishResult result;
        var (db2, scope2) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (db2)
        {
            var coordinator = new PublishCoordinator(db2, scope2, mock);
            result = await coordinator.PublishAsync(Request(contentItemId), runId, _fixture.BrandA);
        }

        Assert.Equal(PublishStatus.Published, result.Status);
        Assert.Equal(1, mock.PublishedMediaCount);            // exactly one post
        Assert.Equal(2, mock.ContainerCount);                 // the first create left an orphan container
    }

    [Fact]
    public async Task Finalized_record_short_circuits_with_no_publish()
    {
        var contentItemId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        const string existingRef = "mock://meta/preexisting";

        // Seed a finalized record under the brand scope.
        var (seedDb, seedScope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (seedDb)
        {
            await using var handle = await seedScope.BeginAsync();
            seedDb.PublishRecords.Add(new PublishRecord
            {
                Id = Guid.NewGuid(),
                BrandId = _fixture.BrandA,
                AgentRunId = runId,
                ContentItemId = contentItemId,
                CreationId = "mock-container-seed",
                Status = PublishStatus.Published,
                ExternalRef = existingRef,
                AttemptCount = 1,
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await seedDb.SaveChangesAsync();
            await handle.CompleteAsync();
        }

        var mock = new MockMetaIntegration();
        PublishResult result;
        var (db, scope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (db)
        {
            var coordinator = new PublishCoordinator(db, scope, mock);
            result = await coordinator.PublishAsync(Request(contentItemId), runId, _fixture.BrandA);
        }

        Assert.Equal(PublishStatus.Published, result.Status);
        Assert.Equal(existingRef, result.ExternalRef);
        Assert.Equal(0, mock.PublishedMediaCount);  // no publish performed
        Assert.Equal(0, mock.ContainerCount);        // no container created
    }

    [Fact]
    public async Task Clean_run_publishes_and_classifies_published()
    {
        var result = await RunOnceAsync(new MockMetaIntegration());

        Assert.Equal(PublishStatus.Published, result.Status);
        Assert.StartsWith("mock://meta/", result.ExternalRef!);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task Transient_publish_failure_is_classified_transient()
    {
        var result = await RunOnceAsync(new MockMetaIntegration { FailPublishWith = PublishStatus.TransientFailure });

        Assert.Equal(PublishStatus.TransientFailure, result.Status);
        Assert.Null(result.ExternalRef);
    }

    [Fact]
    public async Task Terminal_create_failure_is_classified_terminal()
    {
        var result = await RunOnceAsync(new MockMetaIntegration { FailCreateWith = PublishStatus.TerminalFailure });

        Assert.Equal(PublishStatus.TerminalFailure, result.Status);
        Assert.Null(result.ExternalRef);
    }

    private async Task<PublishResult> RunOnceAsync(MockMetaIntegration mock)
    {
        var contentItemId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var (db, scope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (db)
        {
            var coordinator = new PublishCoordinator(db, scope, mock);
            return await coordinator.PublishAsync(Request(contentItemId), runId, _fixture.BrandA);
        }
    }

    private async Task<PublishRecord?> ReadRecordAsync(Guid contentItemId)
    {
        var (db, scope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();
            var record = await db.PublishRecords.AsNoTracking()
                .FirstOrDefaultAsync(r => r.ContentItemId == contentItemId);
            await handle.CompleteAsync();
            return record;
        }
    }
}
