using Backend.Core.Generation;
using Backend.Core.Generation.Cost;
using Backend.Core.Generation.PlatformConstraints;
using Backend.Core.Integrations;
using Backend.Core.Knowledge;
using Backend.Core.Orchestration;
using Backend.Core.Storage;
using Backend.Infrastructure.Integrations.Gemini;
using Microsoft.Extensions.Logging;

namespace Backend.Infrastructure.Orchestration.Maf;

/// <summary>
/// The bundle of dependencies the real generation agents consume (all built in P1, registered in
/// <c>AddGeneration</c>). Bundled so <see cref="MafOrchestrator"/> and
/// <see cref="GenerationWorkflowFactory"/> stay tidy and tests construct one object. The
/// <see cref="Retrieval"/> service is scoped and resolved inside the job's brand scope, so agents
/// ground under the RLS binding. Model ids are config-bound (Sonnet for Strategist/selection/CD,
/// Haiku for Copywriting); <see cref="GlobalCeilingUsd"/> is the Media gate's hard ceiling (DL-029).
/// </summary>
public sealed record GenerationAgentDeps(
    IStructuredGenerator Generator,
    IRetrievalService Retrieval,
    IMediaGenerationTool Media,
    IStorageService Storage,
    PlatformConstraintSet Constraints,
    CostPrices Prices,
    decimal GlobalCeilingUsd,
    string SonnetModel,
    string HaikuModel,
    ITrace Trace,
    ILoggerFactory LoggerFactory,
    VeoOperationStore VeoStore,
    decimal VideoPricePerSec = 0m,
    int MaxVideoDurationSec = 5);
