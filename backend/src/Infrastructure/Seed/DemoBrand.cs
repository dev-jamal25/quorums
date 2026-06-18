using Backend.Core.Onboarding;
using Backend.Infrastructure.Knowledge.Seed;

namespace Backend.Infrastructure.Seed;

/// <summary>
/// The coffee-roaster demo brand identity — a RICH <see cref="BrandOnboardingCommand"/> that actually
/// drives generation (the Content Strategist's angles come from this profile, not a skeleton). It is
/// aligned with <see cref="CoffeeRoasterCorpus"/> — same voice, audience, and content pillars — so the
/// brand, its pillars, and its knowledge corpus tell one coherent story. <see cref="Name"/> is the
/// idempotency key the seeder checks before onboarding.
/// </summary>
public static class DemoBrand
{
    /// <summary>The demo brand's display name — also the seeder's idempotency key (skip-if-exists).</summary>
    public const string Name = "Lighthouse Coffee Roasters";

    public static BrandOnboardingCommand Command { get; } = new(
        Name: Name,
        Positioning: "Single-origin coffee, honestly roasted — provenance you can taste, without the snobbery.",
        ToneDescriptors: ["warm", "unpretentious", "knowledgeable", "inviting"],
        VoiceDo:
        [
            "talk about coffee the way a friend would over a slow morning",
            "be specific and honest about origin, process, and roast level",
            "celebrate the craft and the farmers behind the cup",
        ],
        VoiceDont:
        [
            "use snobby, gatekeeping, or jargon-heavy language",
            "lead with discounts, hype, or empty superlatives",
            "overpromise or talk down to beginners",
        ],
        // Earthy palette: espresso, kraft, cream, sage.
        ColorHexes: ["#3B2A20", "#C8A27C", "#E9DFD2", "#7A8450"],
        ImageryStyle:
            "Earthy and warm: kraft tones, natural light, hands and steam, texture over gloss. The roast "
            + "color must read true; favour candid ritual moments over staged product gloss.",
        // Origin / Craft / Ritual — reuse the corpus constant so the brand's pillars ARE the Strategist's
        // validation contract (DL-026, DL-034 R7) and match the seeded knowledge.
        ContentPillars: [.. CoffeeRoasterCorpus.ContentPillars],
        AudienceSegments: ["curious home brewers", "weekend pour-over enthusiasts", "provenance-minded gift buyers"],
        AudiencePainPoints:
        [
            "overwhelmed by coffee jargon and snobbery",
            "unsure how to brew cafe-quality coffee at home",
            "want to trust where their beans come from and who grew them",
        ],
        ProductContext:
            "A specialty coffee roaster selling single-origin beans and small-batch blends direct to home "
            + "brewers. The lineup centres on an Ethiopia Yirgacheffe single origin (floral, jasmine, bergamot, "
            + "light roast), a Sunrise Espresso Blend (chocolate, caramel, syrupy body, medium-dark), and a "
            + "Swiss-Water Midnight Decaf. Roasting philosophy is honest and origin-forward, with fair pay for "
            + "farmers; content should ground in real provenance, brewing ritual, and sustainability — never discounts.");
}
