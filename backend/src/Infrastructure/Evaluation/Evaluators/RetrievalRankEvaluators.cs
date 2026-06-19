using Microsoft.Extensions.AI.Evaluation;

namespace Backend.Infrastructure.Evaluation.Evaluators;

/// <summary>
/// The deterministic, reference-based retrieval rank evaluators (evaluators.md §2, DL-048) as custom
/// Microsoft.Extensions.AI.Evaluation <see cref="IEvaluator"/>s — no LLM. Context recall@1/@3, Hit@1, MRR,
/// and the rank-aware Context Precision (the primary stage-discriminating metric). Used by the retrieval
/// eval harness to score each golden query and by the paired stage ablation (DL-025).
/// </summary>
public static class RetrievalRankEvaluators
{
    public static IReadOnlyList<IEvaluator> All() =>
    [
        new ContextRecallEvaluator(1),
        new ContextRecallEvaluator(3),
        new HitAtOneEvaluator(),
        new ReciprocalRankEvaluator(),
        new ContextPrecisionEvaluator(),
    ];
}
