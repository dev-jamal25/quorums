using System.Text.Json;
using Backend.Core.Domain;
using Backend.Core.Generation;
using Backend.Core.Generation.Prompting;
using Backend.Core.Generation.Validation;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Backend.Infrastructure.Generation;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;

namespace Backend.Infrastructure.Orchestration.Maf.Nodes;

/// <summary>
/// Content Strategist (DL-019/027) — a <b>fatal</b> node. Grounds via <see cref="AgentGrounding"/>
/// (RLS-scoped), emits N=3 self-rationalizing <see cref="StrategyCandidates"/> through the forced-tool
/// generator (validate-on-receipt + 2-retry → <c>ToolError</c>), validates each <c>pillar</c> against
/// the brand's <see cref="RunState.ContentPillars"/> (R7), and reconciles each candidate's grounding
/// against the injected provenance ids (R6 — never trusts the model's <c>grounded</c>). Retry
/// exhaustion (or IChatClient down) → <see cref="RunState.FatalError"/> (no content without strategy).
/// </summary>
public sealed partial class ContentStrategistExecutor : Executor<RunState, RunState>
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Strategist: no structured pillars — pillar validation skipped for run {RunId}.")]
    private partial void LogNoPillars(Guid runId);

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private static readonly DocType[] _docTypes =
    [
        DocType.BrandPlaybook, DocType.HistoricalPost, DocType.Product, DocType.MarketIntel, DocType.PlatformGuidance,
    ];

    private const string ToolName = "record_strategy_candidates";
    private const string Brief = "Create the brand's next on-brand Instagram feed post.";

    private readonly GenerationAgentDeps _deps;
    private readonly ILogger<ContentStrategistExecutor> _logger;

    public ContentStrategistExecutor(GenerationAgentDeps deps)
        : base("content-strategist")
    {
        _deps = deps;
        _logger = deps.LoggerFactory.CreateLogger<ContentStrategistExecutor>();
    }

    public override ValueTask<RunState> HandleAsync(
        RunState message, IWorkflowContext context, CancellationToken cancellationToken = default)
        => new(RunAsync(message, cancellationToken));

    public async Task<RunState> RunAsync(RunState state, CancellationToken cancellationToken = default)
    {
        if (state.FatalError is not null)
        {
            return state;
        }

        var startedAt = DateTimeOffset.UtcNow;

        var chunks = await AgentGrounding.RetrieveAsync(
            _deps.Retrieval, state.BrandId,
            "brand mission, audience persona, products, historical performance, and market positioning",
            _docTypes).ConfigureAwait(false);
        var provenanceIds = PromptSkeleton.ProvenanceIds(chunks);

        var pillars = state.ContentPillars;
        if (pillars.Count == 0)
        {
            LogNoPillars(state.RunId);
        }

        var prompt = PromptSkeleton.Build(new AgentPromptParts(
            RoleMandate:
                "You are the Content Strategist. You own WHAT to say — the content pillar, marketing angle, " +
                "and objective. Never touch visuals, copy wording, or hashtags.",
            GroundingChunks: chunks,
            InputSliceJson: JsonSerializer.Serialize(new { brandId = state.BrandId, brief = Brief, pillars }, _json),
            Task:
                "Produce exactly 3 genuinely distinct candidate strategies (not paraphrases), each with its own " +
                $"one-line angleRationale. Each pillar MUST be one of: [{string.Join(", ", pillars)}]. " +
                "objective is one of: awareness, engagement, conversion, traffic, retention.",
            Constraints: [],
            ToolName: ToolName));

        var request = new StructuredGenerationRequest<StrategyCandidates>(
            Prompt: prompt,
            ToolName: ToolName,
            ToolDescription: "Record the 3 candidate content strategies.",
            ModelId: _deps.SonnetModel,
            Validate: candidates => ValidateCandidates(candidates, pillars))
        {
            MaxOutputTokens = 2048,
        };

        var outcome = await _deps.Generator.GenerateAsync(request, cancellationToken).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            var failTrace = await _deps.Trace.RecordAsync(
                state.Trace, state.RunId, state.BrandId, "strategy", null, "error",
                startedAt, DateTimeOffset.UtcNow, outcome.Error.Message, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return state with
            {
                FatalError = outcome.Error,
                Errors = [.. state.Errors, outcome.Error],
                Trace = failTrace,
            };
        }

        // DL-054: durably record the raw per-node provenance — the model's claimed ids (union across
        // candidates, as received) and the injected ids — BEFORE Reconcile overwrites chunkIdsUsed.
        var claimedChunkIds = outcome.Value.Candidates
            .SelectMany(candidate => candidate.Grounding.ChunkIdsUsed ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var provenanceTrace = await GroundingProvenance.RecordAsync(
            _deps.Trace, state.Trace, state.RunId, state.BrandId, "strategy",
            claimedChunkIds, provenanceIds, cancellationToken).ConfigureAwait(false);

        var reconciled = outcome.Value.Candidates
            .Select(candidate => candidate with
            {
                Grounding = GroundingValidator.Reconcile(candidate.Grounding, provenanceIds),
            })
            .ToList();

        var cost = NodeCostEstimator.ForCall("strategy", "content_strategist", _deps.Prices);
        var trace = await _deps.Trace.RecordAsync(
            provenanceTrace, state.RunId, state.BrandId, "strategy", null, "ok",
            startedAt, DateTimeOffset.UtcNow, null, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return state with
        {
            Candidates = new StrategyCandidates(reconciled),
            IncurredCosts = [.. state.IncurredCosts, cost],
            Trace = trace,
        };
    }

    private static ValidationResult ValidateCandidates(StrategyCandidates candidates, IReadOnlyList<string> pillars)
    {
        if (candidates.Candidates.Count != 3)
        {
            return ValidationResult.Invalid($"expected exactly 3 candidates, got {candidates.Candidates.Count}");
        }

        foreach (var candidate in candidates.Candidates)
        {
            if (PillarValidator.Check(candidate.Pillar, pillars) == PillarStatus.NotInList)
            {
                return ValidationResult.Invalid(PillarValidator.DescribeMiss(candidate.Pillar, pillars));
            }
        }

        return ValidationResult.Valid;
    }
}
