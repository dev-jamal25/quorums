using System.Text.Json;
using Backend.Core.Domain;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Backend.Infrastructure.Integrations.Meta;
using Backend.Infrastructure.Jobs;
using Backend.IntegrationTests.Durability;
using Backend.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Backend.IntegrationTests.Publishing;

/// <summary>
/// The Slice-4 acceptance gate for the wired publish node (DL-035/038/039). Drives the real
/// <c>ResumeRun â†’ PublishingExecutor â†’ PublishCoordinator</c> over a real RLS-bound Postgres with the
/// crash-modeling mock: happy publish, the edit overlay BY FIELD-PRESENCE, the publish-time re-check,
/// the failure taxonomy (transient retry / exhaustion / terminal), the resumable-state backstop, and
/// crash re-entry through the wiring. Pure crash-idempotency lives in the Slice-2 coordinator tests.
/// </summary>
[Trait("Category", "Publish")]
[Collection("Durability")]
public sealed class PublishNodeTests
{
    private static readonly string[] _draftTags = ["#draft"];
    private static readonly string[] _editedTags = ["#edited"];

    private readonly DurabilityFixture _fixture;

    public PublishNodeTests(DurabilityFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Happy_publish_finalizes_and_uses_the_draft_caption()
    {
        var runId = await SeedPublishableRunAsync("Draft hook", "Draft body", _draftTags);
        await SeedApprovalAsync(runId, ApprovalActionType.Approve);

        var mock = new MockMetaIntegration();
        await ResumeAsync(runId, mock);

        Assert.Equal(RunStatus.Done, await _fixture.ReadRunStatusAsync(runId, _fixture.BrandA));
        Assert.Equal(1, mock.PublishedMediaCount);
        // Hashtags publish INSIDE the caption (DL-055) — the wire caption carries them, composed.
        Assert.Equal("Draft hook\n\nDraft body\n\n#draft", mock.LastRequest!.Caption);
        Assert.Contains("#draft", mock.LastRequest.Caption, StringComparison.Ordinal);
        Assert.Equal(_draftTags, mock.LastRequest.Hashtags);

        var record = await ReadPublishRecordAsync(runId);
        Assert.Equal(PublishStatus.Published, record!.Status);
        Assert.False(string.IsNullOrEmpty(record.ExternalRef));
    }

    [Fact]
    public async Task Approve_with_edit_publishes_the_edited_content()
    {
        var runId = await SeedPublishableRunAsync("Draft hook", "Draft body", _draftTags);
        await SeedApprovalAsync(runId, ApprovalActionType.ApproveWithEdit, "EDITED caption", _editedTags);

        var mock = new MockMetaIntegration();
        await ResumeAsync(runId, mock);

        Assert.Equal("EDITED caption\n\n#edited", mock.LastRequest!.Caption);
        Assert.Equal(_editedTags, mock.LastRequest.Hashtags);
        Assert.Equal(RunStatus.Done, await _fixture.ReadRunStatusAsync(runId, _fixture.BrandA));
    }

    [Fact]
    public async Task Overlay_is_by_field_presence_not_the_action_type_label()
    {
        // The row is typed ApproveWithSchedule but carries edits â€” the edited caption MUST win.
        var runId = await SeedPublishableRunAsync("Draft hook", "Draft body", _draftTags);
        await SeedApprovalAsync(runId, ApprovalActionType.ApproveWithSchedule, "SCHEDULED yet edited", _editedTags);

        var mock = new MockMetaIntegration();
        await ResumeAsync(runId, mock);

        Assert.Equal("SCHEDULED yet edited\n\n#edited", mock.LastRequest!.Caption);
        Assert.Equal(_editedTags, mock.LastRequest.Hashtags);
    }

    [Fact]
    public async Task Publish_time_recheck_failure_is_terminal_with_no_publish()
    {
        var runId = await SeedPublishableRunAsync("ok", "ok", _draftTags);
        await SeedApprovalAsync(runId, ApprovalActionType.ApproveWithEdit, new string('x', 2201), null);

        var mock = new MockMetaIntegration();
        await ResumeAsync(runId, mock);

        Assert.Equal(RunStatus.Failed, await _fixture.ReadRunStatusAsync(runId, _fixture.BrandA));
        Assert.Equal(0, mock.PublishAttemptCount);               // re-check fails before any publish
        Assert.Null(await ReadPublishRecordAsync(runId));        // the coordinator was never called

        var state = await _fixture.ReadCheckpointStateAsync(runId, _fixture.BrandA);
        Assert.Contains(state!.Errors, e => e.Code == "publish.constraint_violation");
    }

    [Fact]
    public async Task Combined_caption_plus_hashtags_over_limit_is_terminal_with_no_publish()
    {
        // Caption alone fits (2198 <= 2200) but caption + the composed "#draft" exceeds 2200 — the
        // combined publish-time check must fail before any Meta call (DL-055).
        var runId = await SeedPublishableRunAsync(new string('x', 2195), "y", _draftTags);
        await SeedApprovalAsync(runId, ApprovalActionType.Approve);

        var mock = new MockMetaIntegration();
        await ResumeAsync(runId, mock);

        Assert.Equal(RunStatus.Failed, await _fixture.ReadRunStatusAsync(runId, _fixture.BrandA));
        Assert.Equal(0, mock.PublishAttemptCount);               // never reached Meta
        var state = await _fixture.ReadCheckpointStateAsync(runId, _fixture.BrandA);
        Assert.Contains(state!.Errors, e => e.Code == "publish.constraint_violation");
    }

    [Fact]
    public async Task Transient_then_published_yields_exactly_one_post()
    {
        var runId = await SeedPublishableRunAsync("h", "b", _draftTags);
        var mock = new MockMetaIntegration { FailPublishWith = PublishStatus.TransientFailure };

        var (db, job) = _fixture.CreateResumeRunJob(_fixture.BrandA, mock);
        await using (db)
        {
            await Assert.ThrowsAsync<TransientPublishException>(() => job.ExecuteAsync(runId, _fixture.BrandA, 0));
            mock.FailPublishWith = null;
            await job.ExecuteAsync(runId, _fixture.BrandA, 1);
        }

        Assert.Equal(RunStatus.Done, await _fixture.ReadRunStatusAsync(runId, _fixture.BrandA));
        Assert.Equal(1, mock.PublishedMediaCount);
    }

    [Fact]
    public async Task Transient_exhausted_ends_failed_not_publishing()
    {
        var runId = await SeedPublishableRunAsync("h", "b", _draftTags);
        var mock = new MockMetaIntegration { FailPublishWith = PublishStatus.TransientFailure };

        var (db, job) = _fixture.CreateResumeRunJob(_fixture.BrandA, mock);
        await using (db)
        {
            await Assert.ThrowsAsync<TransientPublishException>(() => job.ExecuteAsync(runId, _fixture.BrandA, 0));
            await Assert.ThrowsAsync<TransientPublishException>(() => job.ExecuteAsync(runId, _fixture.BrandA, 1));
            await Assert.ThrowsAsync<TransientPublishException>(() => job.ExecuteAsync(runId, _fixture.BrandA, 2));
            await job.ExecuteAsync(runId, _fixture.BrandA, 3); // final allotted attempt â†’ Failed, no throw
        }

        Assert.Equal(RunStatus.Failed, await _fixture.ReadRunStatusAsync(runId, _fixture.BrandA));
        Assert.Equal(4, mock.PublishAttemptCount); // attempts 0..3 then fail â€” pins the off-by-one
        Assert.Equal(0, mock.PublishedMediaCount);
    }

    [Fact]
    public async Task Terminal_failure_ends_failed_with_zero_retries()
    {
        var runId = await SeedPublishableRunAsync("h", "b", _draftTags);
        var mock = new MockMetaIntegration { FailPublishWith = PublishStatus.TerminalFailure };

        await ResumeAsync(runId, mock); // returns normally (no throw)

        Assert.Equal(RunStatus.Failed, await _fixture.ReadRunStatusAsync(runId, _fixture.BrandA));
        Assert.Equal(1, mock.PublishAttemptCount); // attempted once, no retries
    }

    [Fact]
    public async Task Backstop_no_ops_on_a_cancelled_run()
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA, RunStatus.Cancelled);
        var mock = new MockMetaIntegration();

        await ResumeAsync(runId, mock); // no throw, no publish

        Assert.Equal(RunStatus.Cancelled, await _fixture.ReadRunStatusAsync(runId, _fixture.BrandA));
        Assert.Equal(0, mock.PublishAttemptCount);
    }

    [Fact]
    public async Task Backstop_no_double_post_on_a_done_run()
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA, RunStatus.Done);
        var mock = new MockMetaIntegration();

        await ResumeAsync(runId, mock);

        Assert.Equal(RunStatus.Done, await _fixture.ReadRunStatusAsync(runId, _fixture.BrandA));
        Assert.Equal(0, mock.PublishAttemptCount);
    }

    [Fact]
    public async Task Crash_after_publish_then_retry_yields_one_post_through_the_wiring()
    {
        var runId = await SeedPublishableRunAsync("h", "b", _draftTags);
        await SeedApprovalAsync(runId, ApprovalActionType.Approve);
        var mock = new MockMetaIntegration { CrashAfterPublishOnce = true };

        var (db, job) = _fixture.CreateResumeRunJob(_fixture.BrandA, mock);
        await using (db)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => job.ExecuteAsync(runId, _fixture.BrandA, 0)); // crash propagates
            await job.ExecuteAsync(runId, _fixture.BrandA, 1);                                          // retry recovers
        }

        Assert.Equal(RunStatus.Done, await _fixture.ReadRunStatusAsync(runId, _fixture.BrandA));
        Assert.Equal(1, mock.PublishedMediaCount);
        var record = await ReadPublishRecordAsync(runId);
        Assert.Equal(PublishStatus.Published, record!.Status);
        Assert.False(string.IsNullOrEmpty(record.ExternalRef));
    }

    // --- helpers -------------------------------------------------------------------------------

    private async Task<Guid> SeedPublishableRunAsync(string hook, string body, IReadOnlyList<string> hashtags)
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA, RunStatus.Publishing);
        var caption = new Caption(hook, body, hashtags, new Grounding(false, [], Confidence.Low));
        var draft = new ContentItemDraft(caption, MediaRef: null, _fixture.BrandA, Status: "approved");
        var state = TestGeneration.Seed(runId, _fixture.BrandA) with
        {
            Phase = GraphPhase.AwaitingApproval,
            Caption = caption,
            Draft = draft,
        };
        await _fixture.SeedCheckpointAsync(runId, _fixture.BrandA, JsonSerializer.Serialize(state, RunStateJsonOptions.Options));
        return runId;
    }

    private async Task SeedApprovalAsync(
        Guid runId, ApprovalActionType action, string? editedCaption = null, IReadOnlyList<string>? editedHashtags = null)
    {
        var (db, scope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();
            db.ApprovalActions.Add(new ApprovalAction
            {
                Id = Guid.NewGuid(),
                BrandId = _fixture.BrandA,
                AgentRunId = runId,
                Action = action,
                Actor = "human",
                OccurredAt = DateTimeOffset.UtcNow,
                EditedCaption = editedCaption,
                EditedHashtags = editedHashtags?.ToList(),
            });
            await db.SaveChangesAsync();
            await handle.CompleteAsync();
        }
    }

    private async Task ResumeAsync(Guid runId, MockMetaIntegration mock)
    {
        var (db, job) = _fixture.CreateResumeRunJob(_fixture.BrandA, mock);
        await using (db)
        {
            await job.ExecuteAsync(runId, _fixture.BrandA);
        }
    }

    private async Task<PublishRecord?> ReadPublishRecordAsync(Guid contentItemId)
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
