namespace Backend.Core.Domain;

/// <summary>The five corpus document types (DL-026). Drives the chunker dispatch,
/// the chunk metadata shape, and the retrieval pre-filter — kept in lock-step.</summary>
public enum DocType
{
    BrandPlaybook,
    HistoricalPost,
    Product,
    MarketIntel,
    PlatformGuidance,
}
