using Backend.Core.Domain;
using Backend.Core.Knowledge;

namespace Backend.Infrastructure.Knowledge.Seed;

/// <summary>One corpus document to seed: its type, optional facet, title, content, and
/// structured metadata (promoted onto chunks at ingest).</summary>
public sealed record KnowledgeDocSpec(
    DocType DocType,
    KnowledgeFacet? Facet,
    string Title,
    string Content,
    string? Source,
    KnowledgeChunkMetadata? Metadata);

/// <summary>
/// A repeatable coffee-roaster demo corpus. The same specs seed every brand (the brand id
/// only scopes the doc id), so the leakage proof is separated by RLS alone, while the
/// distinctive Yirgacheffe product gives the dense-relevance proof a unique target.
/// historical_post specs carry real, non-null engagement_rate / ctr / audience_segment —
/// required for slice 3's reranker performance blend.
/// </summary>
public static class CoffeeRoasterCorpus
{
    /// <summary>The relevance proof's target doc title (a whole-unit product → chunk index 0).</summary>
    public const string RelevanceProductTitle = "Ethiopia Yirgacheffe Single Origin";

    /// <summary>A query built from the target product's distinctive vocabulary.</summary>
    public const string RelevanceQuery = "yirgacheffe floral jasmine single origin bergamot";

    private static readonly DateTimeOffset _seedDate = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    public static IReadOnlyList<KnowledgeDocSpec> Specs { get; } =
    [
        new(DocType.BrandPlaybook, KnowledgeFacet.Voice, "Brand Voice",
            "Our brand voice is warm, approachable, and unpretentious. We talk about coffee the way a " +
            "friend would over a slow morning — never snobby. Keep the roast and style language honest " +
            "and specific; celebrate the craft without the jargon.",
            "playbook/voice.md", null),

        new(DocType.BrandPlaybook, KnowledgeFacet.Persona, "Audience Persona",
            "Our core persona is the curious home brewer: cares about provenance and ritual, brews pour-over " +
            "on weekends, follows roasters for stories not discounts.",
            "playbook/persona.md", null),

        new(DocType.BrandPlaybook, KnowledgeFacet.Mission, "Mission",
            "Our mission is to make single-origin coffee approachable and to pay farmers fairly for the craft.",
            "playbook/mission.md", null),

        new(DocType.BrandPlaybook, KnowledgeFacet.VisualStyle, "Visual Style",
            "Visual style is earthy and warm: kraft tones, natural light, hands and steam. Photography style " +
            "favors texture over gloss; the roast color should read true.",
            "playbook/visual.md", null),

        new(DocType.Product, null, RelevanceProductTitle,
            "Ethiopia Yirgacheffe single origin. Delicate floral aromatics with jasmine and bergamot, bright " +
            "citrus acidity, and a clean tea-like finish. Light roast, washed process.",
            null,
            new KnowledgeChunkMetadata { ProductId = "yirg-001", Price = 18.50m, Category = "single-origin" }),

        new(DocType.Product, null, "Sunrise Espresso Blend",
            "Sunrise espresso blend. Chocolate, caramel, and toasted nut with a syrupy body. Medium-dark roast, " +
            "built to pull sweet and balanced.",
            null,
            new KnowledgeChunkMetadata { ProductId = "esp-001", Price = 16.00m, Category = "blend" }),

        new(DocType.Product, null, "Midnight Decaf",
            "Midnight decaf. Swiss Water process, smooth cocoa and brown sugar, low acidity for late evenings.",
            null,
            new KnowledgeChunkMetadata { ProductId = "dec-001", Price = 17.00m, Category = "decaf" }),

        new(DocType.HistoricalPost, null, "Post - Pour Over Sunday",
            "A slow Sunday pour-over ritual with our Ethiopia Yirgacheffe — bloom, pour, breathe.",
            null,
            new KnowledgeChunkMetadata
            {
                EngagementRate = 0.071,
                Ctr = 0.034,
                AudienceSegment = "enthusiasts",
                Objective = "engagement",
                Date = _seedDate,
            }),

        new(DocType.HistoricalPost, null, "Post - Espresso Tutorial",
            "Dialing in the perfect espresso shot: grind, dose, and time, one variable at a time.",
            null,
            new KnowledgeChunkMetadata
            {
                EngagementRate = 0.052,
                Ctr = 0.028,
                AudienceSegment = "beginners",
                Objective = "education",
                Date = _seedDate,
            }),

        new(DocType.PlatformGuidance, null, "Reels Retention",
            "Reels under 15 seconds retain better for our audience; open on the pour, not the logo.",
            null,
            new KnowledgeChunkMetadata { Platform = "instagram", Surface = "reel" }),

        new(DocType.PlatformGuidance, null, "Feed Cadence",
            "Post to the feed about three times a week, mid-morning, when our home brewers are scrolling.",
            null,
            new KnowledgeChunkMetadata { Platform = "instagram", Surface = "feed" }),
    ];
}
