using System.Text.Json;
using Backend.Core.Generation;
using Backend.Core.Generation.Prompting;
using Backend.Core.Generation.Validation;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Backend.Infrastructure.Generation;
using Microsoft.Agents.AI.Workflows;

namespace Backend.Infrastructure.Orchestration.Maf.Nodes;

/// <summary>
/// The Supervisor's multi-angle selection — the ONE LLM synthesis call of the hybrid Supervisor
/// (DL-027), a control-plane step inserted between Strategist and Creative Director (not a peer
/// agent). It does NOT write Phase/Draft/Budget. Takes the 3 candidates → <see cref="SelectionDecision"/>
/// via the generator; <see cref="SelectionValidator"/> enforces <c>chosenIndex ∈ [0, N)</c>. The
/// chosen candidate becomes <see cref="RunState.Strategy"/>; the 3 candidates + chosenIndex + rationale
/// persist in the span <c>Detail</c> (the eval-able evidence). Exhaustion → <see cref="RunState.FatalError"/>
/// (no strategy → the Creative Director cannot run).
/// </summary>
public sealed class SupervisorSelectionExecutor : Executor<RunState, RunState>
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private const string ToolName = "record_selection";

    private readonly GenerationAgentDeps _deps;

    public SupervisorSelectionExecutor(GenerationAgentDeps deps)
        : base("supervisor-selection") => _deps = deps;

    public override ValueTask<RunState> HandleAsync(
        RunState message, IWorkflowContext context, CancellationToken cancellationToken = default)
        => new(RunAsync(message, cancellationToken));

    public async Task<RunState> RunAsync(RunState state, CancellationToken cancellationToken = default)
    {
        if (state.FatalError is not null || state.Candidates is null)
        {
            return state;
        }

        var candidates = state.Candidates.Candidates;
        var startedAt = DateTimeOffset.UtcNow;

        var prompt = PromptSkeleton.Build(new AgentPromptParts(
            RoleMandate:
                "You are the Supervisor choosing the single best content strategy for this brand and brief. " +
                "Do not rewrite or merge candidates — select one index and justify the choice in one line.",
            GroundingChunks: [],
            InputSliceJson: JsonSerializer.Serialize(new { candidates }, _json),
            Task: $"Choose the best candidate. chosenIndex MUST be in [0, {candidates.Count}).",
            Constraints: [],
            ToolName: ToolName));

        var request = new StructuredGenerationRequest<SelectionDecision>(
            Prompt: prompt,
            ToolName: ToolName,
            ToolDescription: "Record the chosen strategy index and a one-line rationale.",
            ModelId: _deps.SonnetModel,
            Validate: decision => SelectionValidator.Validate(decision.ChosenIndex, candidates.Count));

        var outcome = await _deps.Generator.GenerateAsync(request, cancellationToken).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            var failTrace = await _deps.Trace.RecordAsync(
                state.Trace, state.RunId, state.BrandId, "supervisor-selection", null, "error",
                startedAt, DateTimeOffset.UtcNow, outcome.Error.Message, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return state with
            {
                FatalError = outcome.Error,
                Errors = [.. state.Errors, outcome.Error],
                Trace = failTrace,
            };
        }

        var decision = outcome.Value;
        var chosen = candidates[decision.ChosenIndex];

        // DL-027 evidence: the 3 candidates + chosenIndex + rationale ride in the span Detail.
        var detail = JsonSerializer.Serialize(
            new { candidates, chosenIndex = decision.ChosenIndex, rationale = decision.Rationale }, _json);
        var cost = NodeCostEstimator.ForCall("supervisor-selection", "supervisor_selection", _deps.Prices);
        var trace = await _deps.Trace.RecordAsync(
            state.Trace, state.RunId, state.BrandId, "supervisor-selection", null, "ok",
            startedAt, DateTimeOffset.UtcNow, null, detail, cancellationToken)
            .ConfigureAwait(false);

        return state with
        {
            Strategy = chosen,
            IncurredCosts = [.. state.IncurredCosts, cost],
            Trace = trace,
        };
    }
}
