using Backend.Api.Controllers;
using Backend.Api.Dtos;
using Backend.Core.Domain;
using Backend.Core.Orchestration;
using Backend.Infrastructure.Configuration.Options;
using Backend.Infrastructure.Integrations.Meta;
using Backend.IntegrationTests.Durability;
using Backend.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Xunit;

namespace Backend.IntegrationTests.Publishing;

/// <summary>
/// The Slice-6a acceptance gate for the review projection (DL-040, DL-041). Drives
/// <c>GET /runs/{id}/review</c> + <c>/media</c> over a real RLS-bound Postgres + a deterministic
/// generation segment: the projection assembles (content, grounding, alternatives, BudgetDegraded,
/// timeline), the server-computed available-actions track the status + the regenerate bound (the same
/// <see cref="GateActionPolicy"/> the endpoints enforce), the media is proxied as a renderable asset,
/// and both reads are brand-scoped.
/// </summary>
[Trait("Category", "Review")]
[Collection("Durability")]
public sealed class RunReviewTests
{
    private static readonly IOptions<RegenerationOptions> _maxThree = Options.Create(new RegenerationOptions { MaxPerRun = 3 });

    private readonly DurabilityFixture _fixture;

    public RunReviewTests(DurabilityFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Review_at_the_gate_projects_content_grounding_alternatives_and_actions()
    {
        var storage = new InMemoryStorageService();
        var runId = await GenerateAsync(storage);

        var dto = await ReviewAsync(runId, _fixture.BrandA, storage);

        Assert.Equal(RunStatus.AwaitingApproval, dto.Status);
        Assert.Equal(new[] { GateAction.Approve, GateAction.Regenerate, GateAction.Reject }, dto.AvailableActions);
        Assert.NotNull(dto.Regenerate);
        Assert.Equal(3, dto.Regenerate!.Remaining);
        Assert.Equal(RegenerateModes.All, dto.Regenerate.Modes);

        // "Why this content": the selected angle + the banked N=3 alternatives, with the current marked
        // by its index — what reselect-angle would choose from (DL-027).
        Assert.NotNull(dto.SelectedAngle);
        Assert.Equal(3, dto.AlternativeAngles.Count);
        Assert.InRange(dto.SelectedAngle!.ChosenIndex, 0, dto.AlternativeAngles.Count - 1);
        Assert.NotNull(dto.Grounding);

        Assert.False(string.IsNullOrWhiteSpace(dto.Caption));
        Assert.Empty(dto.Timeline);            // no gate visit yet
        Assert.False(dto.BudgetDegraded);      // the default budget affords media
        Assert.Equal($"runs/{runId}/media", dto.ImageUrl);
    }

    [Fact]
    public async Task Media_proxies_the_generated_asset()
    {
        var storage = new InMemoryStorageService();
        var runId = await GenerateAsync(storage);
        var state = await _fixture.ReadCheckpointStateAsync(runId, _fixture.BrandA);
        var key = state!.Draft!.MediaRef!.StorageKey;

        var (db, scope, brand) = _fixture.CreateGateDeps(_fixture.BrandA);
        await using (db)
        {
            var controller = new RunsController(db, scope, brand, new RecordingBackgroundJobClient(), storage, _maxThree);
            var result = await controller.Media(runId, default);
            var file = Assert.IsType<FileContentResult>(result);
            Assert.NotEmpty(file.FileContents);
            Assert.Equal(storage.Objects[key].Content, file.FileContents);
        }
    }

    [Fact]
    public async Task Review_drops_regenerate_at_the_bound_consistent_with_the_endpoint()
    {
        var storage = new InMemoryStorageService();
        var runId = await GenerateAsync(storage);
        await SeedRegenerateActionsAsync(runId, 3); // at MaxPerRun=3

        var dto = await ReviewAsync(runId, _fixture.BrandA, storage);

        Assert.Equal(new[] { GateAction.Approve, GateAction.Reject }, dto.AvailableActions);
        Assert.Null(dto.Regenerate);
        Assert.Equal(3, dto.Timeline.Count(e => e.Kind == "action")); // the real regenerate rows project into the timeline
    }

    [Fact]
    public async Task Review_after_publish_reflects_the_outcome_and_timeline()
    {
        var storage = new InMemoryStorageService();
        var runId = await GenerateAsync(storage);

        var (gdb, gscope, gbrand) = _fixture.CreateGateDeps(_fixture.BrandA);
        await using (gdb)
        {
            var controller = new RunsController(gdb, gscope, gbrand, new RecordingBackgroundJobClient(), storage, _maxThree);
            await controller.Approval(runId, new ApprovalRequest(GateDecision.Approve, null, null, null), default);
        }

        var mock = new MockMetaIntegration();
        var (pdb, pjob) = _fixture.CreateResumeRunJob(_fixture.BrandA, mock);
        await using (pdb)
        {
            await pjob.ExecuteAsync(runId, _fixture.BrandA);
        }

        var dto = await ReviewAsync(runId, _fixture.BrandA, storage);

        Assert.Equal(RunStatus.Done, dto.Status);
        Assert.Empty(dto.AvailableActions); // terminal — no actions
        Assert.Null(dto.Regenerate);

        Assert.Contains(dto.Timeline, e => e.Kind == "action" && e.Label == ApprovalActionType.Approve.ToString());
        var publishEntries = dto.Timeline.Where(e => e.Kind == "publish").ToList();
        var publishEntry = Assert.Single(publishEntries);
        Assert.Equal(PublishStatus.Published.ToString(), publishEntry.Label);
        Assert.False(string.IsNullOrEmpty(publishEntry.Detail)); // the external ref
    }

    [Fact]
    [Trait("Category", "Isolation")]
    public async Task Run_list_is_brand_scoped()
    {
        var storage = new InMemoryStorageService();
        var runId = await GenerateAsync(storage); // BrandA

        Assert.Contains(await ListAsync(_fixture.BrandA), r => r.RunId == runId);
        Assert.DoesNotContain(await ListAsync(_fixture.BrandB), r => r.RunId == runId);
    }

    [Fact]
    [Trait("Category", "Isolation")]
    public async Task Review_and_media_are_brand_scoped()
    {
        var storage = new InMemoryStorageService();
        var runId = await GenerateAsync(storage); // BrandA's run

        // A controller bound to Brand B must not see Brand A's run — the RLS-scoped read returns nothing.
        var (db, scope, brand) = _fixture.CreateGateDeps(_fixture.BrandB);
        await using (db)
        {
            var controller = new RunsController(db, scope, brand, new RecordingBackgroundJobClient(), storage, _maxThree);

            var review = await controller.Review(runId, default);
            Assert.IsType<NotFoundResult>(review.Result);

            var media = await controller.Media(runId, default);
            Assert.IsType<NotFoundResult>(media);
        }
    }

    // --- helpers -------------------------------------------------------------------------------

    private async Task<Guid> GenerateAsync(InMemoryStorageService storage)
    {
        var runId = await _fixture.SeedAgentRunAsync(_fixture.BrandA);
        var (db, job) = _fixture.CreateExecuteRunJob(_fixture.BrandA, TestGeneration.Deps(storage: storage));
        await using (db)
        {
            await job.ExecuteAsync(runId, _fixture.BrandA);
        }

        return runId;
    }

    private async Task<IReadOnlyList<RunSummaryDto>> ListAsync(Guid brandId)
    {
        var (db, scope, brand) = _fixture.CreateGateDeps(brandId);
        await using (db)
        {
            var controller = new RunsController(db, scope, brand, new RecordingBackgroundJobClient(), new InMemoryStorageService(), _maxThree);
            var action = await controller.List(default);
            return Assert.IsType<List<RunSummaryDto>>(Assert.IsType<OkObjectResult>(action.Result).Value);
        }
    }

    private async Task<RunReviewDto> ReviewAsync(Guid runId, Guid brandId, InMemoryStorageService storage)
    {
        var (db, scope, brand) = _fixture.CreateGateDeps(brandId);
        await using (db)
        {
            var controller = new RunsController(db, scope, brand, new RecordingBackgroundJobClient(), storage, _maxThree);
            var action = await controller.Review(runId, default);
            return Assert.IsType<RunReviewDto>(Assert.IsType<OkObjectResult>(action.Result).Value);
        }
    }

    private async Task SeedRegenerateActionsAsync(Guid runId, int count)
    {
        var (db, scope) = _fixture.CreateReadContext(_fixture.BrandA);
        await using (db)
        {
            await using var handle = await scope.BeginAsync();
            for (var i = 0; i < count; i++)
            {
                db.ApprovalActions.Add(new ApprovalAction
                {
                    Id = Guid.NewGuid(),
                    BrandId = _fixture.BrandA,
                    AgentRunId = runId,
                    Action = ApprovalActionType.Regenerate,
                    Actor = "human",
                    OccurredAt = DateTimeOffset.UtcNow.AddSeconds(i),
                    RegenerateMode = RegenerateModes.SameAngle,
                });
            }

            await db.SaveChangesAsync();
            await handle.CompleteAsync();
        }
    }
}
