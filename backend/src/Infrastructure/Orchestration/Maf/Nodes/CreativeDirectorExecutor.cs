using System.Text.Json;
using Backend.Core.Domain;
using Backend.Core.Generation;
using Backend.Core.Generation.PlatformConstraints;
using Backend.Core.Generation.Prompting;
using Backend.Core.Generation.Validation;
using Backend.Core.Orchestration;
using Backend.Core.Orchestration.Contracts;
using Backend.Infrastructure.Generation;
using Microsoft.Agents.AI.Workflows;

namespace Backend.Infrastructure.Orchestration.Maf.Nodes;

/// <summary>
/// Creative Director (DL-019/027) — a <b>fatal</b> node. Consumes the chosen <see cref="ContentStrategy"/>,
/// grounds for visual direction (RLS-scoped), and produces <see cref="CreativeDirection"/> via the
/// forced-tool generator + grounding reconcile (R6). It then <b>stamps</b>
/// <c>mediaPromptBrief.AspectRatio</c> deterministically from the run's target surface
/// (<see cref="PlatformConstraintValidator.StampAspectRatio"/>), overriding any model value (R8 —
/// informed in the prompt, authoritative in the deterministic layer). Retry exhaustion →
/// <see cref="RunState.FatalError"/>.
/// </summary>
public sealed class CreativeDirectorExecutor : Executor<RunState, RunState>
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private static readonly DocType[] _docTypes =
    [
        DocType.BrandPlaybook, DocType.Product, DocType.PlatformGuidance,
    ];

    private const string ToolName = "record_creative_direction";

    private readonly GenerationAgentDeps _deps;

    public CreativeDirectorExecutor(GenerationAgentDeps deps)
        : base("creative-director") => _deps = deps;

    public override ValueTask<RunState> HandleAsync(
        RunState message, IWorkflowContext context, CancellationToken cancellationToken = default)
        => new(RunAsync(message, cancellationToken));

    public async Task<RunState> RunAsync(RunState state, CancellationToken cancellationToken = default)
    {
        if (state.FatalError is not null || state.Strategy is null)
        {
            return state;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var surface = _deps.Constraints.For(state.TargetSurface);

        var chunks = await AgentGrounding.RetrieveAsync(
            _deps.Retrieval, state.BrandId,
            "brand visual style, imagery, colour palette, and product look",
            _docTypes).ConfigureAwait(false);
        var provenanceIds = PromptSkeleton.ProvenanceIds(chunks);

        var prompt = PromptSkeleton.Build(new AgentPromptParts(
            RoleMandate:
                "You are the Creative Director. You own HOW it looks — the visual concept, style and colour " +
                "tokens, and the structured mediaPromptBrief. Never decide the message or write the caption.",
            GroundingChunks: chunks,
            InputSliceJson: JsonSerializer.Serialize(state.Strategy, _json),
            Task:
                "Produce the visual direction and a structured mediaPromptBrief for the chosen strategy.",
            Constraints:
            [
                $"aspectRatio should be one of [{string.Join(", ", surface.AllowedAspectRatios)}] " +
                "for the target surface (it is stamped deterministically afterward regardless).",
            ],
            ToolName: ToolName));

        var request = new StructuredGenerationRequest<CreativeDirection>(
            Prompt: prompt,
            ToolName: ToolName,
            ToolDescription: "Record the creative direction and structured media-prompt brief.",
            ModelId: _deps.SonnetModel,
            Validate: _ => ValidationResult.Valid)
        {
            MaxOutputTokens = 1536,
        };

        var outcome = await _deps.Generator.GenerateAsync(request, cancellationToken).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            var failTrace = await _deps.Trace.RecordAsync(
                state.Trace, state.RunId, state.BrandId, "creative", null, "error",
                startedAt, DateTimeOffset.UtcNow, outcome.Error.Message, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return state with
            {
                FatalError = outcome.Error,
                Errors = [.. state.Errors, outcome.Error],
                Trace = failTrace,
            };
        }

        // Reconcile grounding (R6), then stamp the aspect ratio from the surface, overriding the model (R8).
        var creative = outcome.Value with
        {
            Grounding = GroundingValidator.Reconcile(outcome.Value.Grounding, provenanceIds),
            MediaPromptBrief = PlatformConstraintValidator.StampAspectRatio(outcome.Value.MediaPromptBrief, surface),
        };

        var cost = NodeCostEstimator.ForCall("creative", "creative_director", _deps.Prices);
        var trace = await _deps.Trace.RecordAsync(
            state.Trace, state.RunId, state.BrandId, "creative", null, "ok",
            startedAt, DateTimeOffset.UtcNow, null, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return state with
        {
            Creative = creative,
            IncurredCosts = [.. state.IncurredCosts, cost],
            Trace = trace,
        };
    }
}
