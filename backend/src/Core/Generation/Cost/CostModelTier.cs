namespace Backend.Core.Generation.Cost;

/// <summary>The text-model tiers the cost model prices (DL-029). Embed/rerank are local (TEI) → $0.</summary>
public enum CostModelTier
{
    Sonnet,
    Haiku,
}
