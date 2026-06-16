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
/// Copywriting (DL-019/027) — forks in parallel with Media. Consumes the chosen strategy + creative
/// direction + voice grounding (RLS-scoped) → <see cref="Caption"/> via the Haiku generator. A draft
/// ALWAYS requires a caption: schema retry-exhaustion → <see cref="RunState.FatalError"/> (the caption
/// is never the degradable asset — only media degrades, R1). Deterministic PlatformConstraints (DL-030):
/// hashtagCount over → repair (drop extras + a trace note); captionLength over → a bounded shorten
/// regenerate, then a hard-truncate fallback so the pipeline never emits an over-limit caption.
/// </summary>
public sealed class CopywritingExecutor : Executor<RunState, RunState>
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private static readonly DocType[] _docTypes =
    [
        DocType.BrandPlaybook, DocType.HistoricalPost, DocType.Product, DocType.PlatformGuidance,
    ];

    private const string ToolName = "record_caption";
    private const int LengthRegenerateAttempts = 2;

    private readonly GenerationAgentDeps _deps;

    public CopywritingExecutor(GenerationAgentDeps deps)
        : base("copywriting") => _deps = deps;

    public override ValueTask<RunState> HandleAsync(
        RunState message, IWorkflowContext context, CancellationToken cancellationToken = default)
        => new(RunAsync(message, cancellationToken));

    public async Task<RunState> RunAsync(RunState state, CancellationToken cancellationToken = default)
    {
        if (state.FatalError is not null || state.Strategy is null || state.Creative is null)
        {
            return state;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var surface = _deps.Constraints.For(state.TargetSurface);

        var chunks = await AgentGrounding.RetrieveAsync(
            _deps.Retrieval, state.BrandId,
            "brand voice, tone, do and dont language, and product details",
            _docTypes).ConfigureAwait(false);
        var provenanceIds = PromptSkeleton.ProvenanceIds(chunks);

        var prompt = PromptSkeleton.Build(new AgentPromptParts(
            RoleMandate:
                "You are the Copywriter. You own the caption — hook, body, hashtags. Never change the strategy " +
                "or the visual direction.",
            GroundingChunks: chunks,
            InputSliceJson: JsonSerializer.Serialize(new { strategy = state.Strategy, creative = state.Creative }, _json),
            Task: "Write the caption for this post.",
            Constraints:
            [
                $"at most {surface.MaxHashtags} hashtags",
                $"caption (hook + body) at most {surface.MaxCaptionLength} characters",
            ],
            ToolName: ToolName));

        var outcome = await GenerateAsync(prompt, cancellationToken).ConfigureAwait(false);
        if (!outcome.Succeeded)
        {
            // The caption is required — exhaustion is fatal (R1).
            var failTrace = await _deps.Trace.RecordAsync(
                state.Trace, state.RunId, state.BrandId, "copywriting", null, "error",
                startedAt, DateTimeOffset.UtcNow, outcome.Error.Message, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return state with
            {
                FatalError = outcome.Error,
                Errors = [.. state.Errors, outcome.Error],
                Trace = failTrace,
            };
        }

        var caption = outcome.Value;

        // captionLength over → regenerate with a shorten hint (DL-030), bounded.
        for (var attempt = 0; attempt < LengthRegenerateAttempts && ProseLength(caption) > surface.MaxCaptionLength; attempt++)
        {
            var shortenPrompt = prompt +
                $"\n\nYour previous caption was {ProseLength(caption)} characters — too long. " +
                $"Rewrite the hook + body to at most {surface.MaxCaptionLength} characters total.";
            var retry = await GenerateAsync(shortenPrompt, cancellationToken).ConfigureAwait(false);
            if (!retry.Succeeded)
            {
                break;
            }

            caption = retry.Value;
        }

        // Hard-truncate fallback — never emit an over-limit caption.
        caption = HardTruncate(caption, surface);

        // hashtagCount over → repair (drop extras + trace note).
        var (hashtags, repaired) = PlatformConstraintValidator.RepairHashtags(caption.Hashtags, surface);
        var finalCaption = caption with
        {
            Hashtags = hashtags,
            Grounding = GroundingValidator.Reconcile(caption.Grounding, provenanceIds),
        };

        var detail = repaired
            ? JsonSerializer.Serialize(new { @event = "HashtagRepaired", limit = surface.MaxHashtags }, _json)
            : null;
        var cost = NodeCostEstimator.ForCall("copywriting", "copywriting", _deps.Prices);
        var trace = await _deps.Trace.RecordAsync(
            state.Trace, state.RunId, state.BrandId, "copywriting", null, "ok",
            startedAt, DateTimeOffset.UtcNow, null, detail, cancellationToken)
            .ConfigureAwait(false);

        return state with
        {
            Caption = finalCaption,
            IncurredCosts = [.. state.IncurredCosts, cost],
            Trace = trace,
        };
    }

    private Task<GenerationOutcome<Caption>> GenerateAsync(string prompt, CancellationToken cancellationToken) =>
        _deps.Generator.GenerateAsync(
            new StructuredGenerationRequest<Caption>(
                Prompt: prompt,
                ToolName: ToolName,
                ToolDescription: "Record the caption (hook, body, hashtags).",
                ModelId: _deps.HaikuModel,
                Validate: _ => ValidationResult.Valid)
            {
                MaxOutputTokens = 1024,
            },
            cancellationToken);

    private static int ProseLength(Caption caption) => caption.Hook.Length + caption.Body.Length;

    private static Caption HardTruncate(Caption caption, SurfaceConstraints surface)
    {
        if (ProseLength(caption) <= surface.MaxCaptionLength)
        {
            return caption;
        }

        var bodyBudget = Math.Max(0, surface.MaxCaptionLength - caption.Hook.Length);
        var body = caption.Body.Length > bodyBudget ? caption.Body[..bodyBudget] : caption.Body;
        var hook = caption.Hook.Length > surface.MaxCaptionLength
            ? caption.Hook[..surface.MaxCaptionLength]
            : caption.Hook;
        return caption with { Hook = hook, Body = body };
    }
}
