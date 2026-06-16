using Backend.Core.Orchestration;
using Backend.Infrastructure.Integrations.Meta;
using Backend.Infrastructure.Orchestration.Maf;
using Backend.IntegrationTests.Support;
using Xunit;

namespace Backend.IntegrationTests.Generation;

/// <summary>
/// The P2 slice proofs at the orchestrator level (deterministic CI mocks, no DB, no live key/Gemini):
/// the happy run, the asymmetric caption-only degrade (the part slice-1 never exercised), the global
/// ceiling and fatal-exhaustion failures, and the empty-RAG ungrounded path. The AgentRun-status
/// mapping and durable resume are proven in the job-level suite.
/// </summary>
public sealed class GenerationPipelineTests
{
    [Fact]
    public async Task Happy_run_reaches_awaiting_approval_with_a_draft_and_full_trace()
    {
        var media = new RecordingMediaGenerationTool();
        var orchestrator = new MafOrchestrator(TestGeneration.Deps(media: media), new MockMetaIntegration());

        var result = await orchestrator.RunGenerationAsync(TestGeneration.Seed(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Null(result.FatalError);
        Assert.Equal(GraphPhase.AwaitingApproval, result.Phase);
        Assert.NotNull(result.Strategy);
        Assert.NotNull(result.Caption);
        Assert.NotNull(result.Media);
        Assert.NotNull(result.Draft);
        Assert.Equal("pending", result.Draft!.Status);
        Assert.Equal(1, media.Calls);

        // A span per node + per tool call, including the 3 candidates + SelectionDecision (DL-027).
        Assert.Contains(result.Trace.Spans, s => s.Node == "strategy");
        Assert.Contains(result.Trace.Spans,
            s => s.Node == "supervisor-selection" && s.Detail != null && s.Detail.Contains("chosenIndex", StringComparison.Ordinal));
        Assert.Contains(result.Trace.Spans, s => s.Node == "creative");
        Assert.Contains(result.Trace.Spans, s => s.Node == "copywriting");
        Assert.Contains(result.Trace.Spans, s => s.Node == "media" && s.Tool == "gemini.generate");
        Assert.Contains(result.Trace.Spans, s => s.Node == "media" && s.Tool == "minio.put");
    }

    [Fact]
    public async Task Media_budget_breach_yields_a_valid_caption_only_draft_with_zero_media_calls()
    {
        var media = new RecordingMediaGenerationTool();
        var orchestrator = new MafOrchestrator(TestGeneration.Deps(media: media), new MockMetaIntegration());

        // MediaBudget = 0 → the pre-Media gate degrades; Copywriting is independent and completes.
        var seed = TestGeneration.Seed(Guid.NewGuid(), Guid.NewGuid(),
            budget: new Budget(TokenBudget: 10_000, TokensSpent: 0, MediaBudget: 0m, MediaSpent: 0m));

        var result = await orchestrator.RunGenerationAsync(seed);

        Assert.Null(result.FatalError);                              // a degrade, not a failure (R1)
        Assert.Equal(GraphPhase.AwaitingApproval, result.Phase);    // reaches the gate
        Assert.NotNull(result.Caption);                             // copy completed — no hung barrier
        Assert.Null(result.Media);
        Assert.NotNull(result.Draft);
        Assert.Equal("degraded-caption-only", result.Draft!.Status);
        Assert.Equal(0, media.Calls);                               // ZERO IMediaGenerationTool calls
        Assert.Contains(result.Trace.Spans,
            s => s.Node == "media" && s.Status == "degraded" && s.Detail != null && s.Detail.Contains("BudgetDegraded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Global_ceiling_trips_on_accumulated_pre_fork_spend_before_any_media_call()
    {
        var media = new RecordingMediaGenerationTool();

        // The Media gate's fork-time snapshot is Σ pre-fork IncurredCosts (R2): Strategist $0.036 +
        // selection $0.00525 + CD $0.01575 = $0.057 (test prices). A $0.05 ceiling sits ABOVE the
        // largest single node ($0.036) and BELOW the accumulated sum ($0.057) — so only the summed
        // pre-fork spend breaches it, and it breaches before the first IMediaGenerationTool call.
        var orchestrator = new MafOrchestrator(
            TestGeneration.Deps(media: media, globalCeilingUsd: 0.05m), new MockMetaIntegration());

        var result = await orchestrator.RunGenerationAsync(TestGeneration.Seed(Guid.NewGuid(), Guid.NewGuid()));

        Assert.NotNull(result.FatalError);
        Assert.Equal("budget.ceiling_exceeded", result.FatalError!.Code);
        Assert.False(result.FatalError.Retryable);
        Assert.Null(result.Draft);
        Assert.Equal(0, media.Calls);                               // breach detected on pre-fork spend alone
    }

    [Fact]
    public async Task Pre_fork_snapshot_excludes_the_pending_media_dollar_so_a_ceiling_above_it_does_not_trip()
    {
        var media = new RecordingMediaGenerationTool();

        // Bracket the other side: $0.057 (pre-fork sum) < $0.07 ceiling < $0.097 (pre-fork + the
        // $0.04 image). The gate snapshots pre-fork spend ONLY — it never pre-charges the pending
        // media dollar — so the run proceeds, the image renders, and total spend overshoots the
        // ceiling without re-checking at the fan-in (the accepted R2 overshoot bound).
        var orchestrator = new MafOrchestrator(
            TestGeneration.Deps(media: media, globalCeilingUsd: 0.07m), new MockMetaIntegration());

        var result = await orchestrator.RunGenerationAsync(TestGeneration.Seed(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Null(result.FatalError);
        Assert.Equal(GraphPhase.AwaitingApproval, result.Phase);
        Assert.NotNull(result.Media);
        Assert.Equal(1, media.Calls);                               // gate passed: pending image not pre-charged
    }

    [Fact]
    public async Task Strategist_retry_exhaustion_fails_the_run()
    {
        var orchestrator = new MafOrchestrator(
            TestGeneration.Deps(failTools: ["record_strategy_candidates"]), new MockMetaIntegration());

        var result = await orchestrator.RunGenerationAsync(TestGeneration.Seed(Guid.NewGuid(), Guid.NewGuid()));

        Assert.NotNull(result.FatalError);
        Assert.Null(result.Strategy);
        Assert.Null(result.Draft);
    }

    [Fact]
    public async Task Copywriting_retry_exhaustion_fails_the_run_because_a_caption_is_required()
    {
        var orchestrator = new MafOrchestrator(
            TestGeneration.Deps(failTools: ["record_caption"]), new MockMetaIntegration());

        var result = await orchestrator.RunGenerationAsync(TestGeneration.Seed(Guid.NewGuid(), Guid.NewGuid()));

        Assert.NotNull(result.FatalError);
        Assert.Null(result.Draft);
    }

    [Fact]
    public async Task Empty_retrieval_proceeds_ungrounded_to_a_lower_confidence_draft()
    {
        var orchestrator = new MafOrchestrator(
            TestGeneration.Deps(retrieval: FakeRetrievalService.Empty()), new MockMetaIntegration());

        var result = await orchestrator.RunGenerationAsync(TestGeneration.Seed(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Null(result.FatalError);
        Assert.Equal(GraphPhase.AwaitingApproval, result.Phase);
        Assert.NotNull(result.Draft);
        Assert.NotNull(result.Strategy);
        Assert.False(result.Strategy!.Grounding.Grounded);   // ungrounded — derived from empty provenance (R6/R8)
        Assert.Empty(result.Strategy.Grounding.ChunkIdsUsed);
    }
}
