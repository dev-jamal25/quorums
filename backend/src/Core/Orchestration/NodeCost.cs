namespace Backend.Core.Orchestration;

/// <summary>
/// One node's incurred cost, returned in its output and reconciled into <see cref="Budget"/> by the
/// Supervisor at the fan-in join (DL-029, DL-034 R3). Costs are static per-call estimates
/// (P1's <c>CostEstimateTable × CostPrices</c>) — deterministic for the budget gate; Langfuse
/// captures real actuals separately (Phase 9). <see cref="Tokens"/> folds into
/// <c>Budget.TokensSpent</c>; <see cref="TokenUsd"/> + <see cref="MediaUsd"/> form the global-$
/// ceiling snapshot; <see cref="MediaUsd"/> folds into <c>Budget.MediaSpent</c>.
/// </summary>
public sealed record NodeCost(string Node, int Tokens, decimal TokenUsd, decimal MediaUsd);
