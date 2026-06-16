namespace Backend.Core.Domain;

/// <summary>
/// The structured brand identity produced by onboarding and consumed per the
/// orchestration contracts by the Content Strategist, Creative Director, and
/// Copywriting agents. One profile per brand (1:1 with <see cref="Brand"/>),
/// brand-scoped and protected by the same Postgres RLS policy as every other
/// brand-owned table (DL-002, DL-007). The brand's display <c>name</c> is the
/// identity name and lives on <see cref="Brand"/>; this entity carries the rest
/// of the identity (positioning, voice, visual tokens, audience, product context).
/// </summary>
public sealed class BrandProfile : IBrandScoped
{
    public Guid Id { get; set; }

    public Guid BrandId { get; set; }

    // --- Identity ---------------------------------------------------------
    /// <summary>The positioning statement / tagline that anchors the brand identity.</summary>
    public string Positioning { get; set; } = default!;

    // --- Voice ------------------------------------------------------------
    /// <summary>Tone descriptors that characterise the brand voice (e.g. "warm", "irreverent").</summary>
    public List<string> ToneDescriptors { get; set; } = [];

    /// <summary>"Do" language guidance: phrasings and moves the voice should use.</summary>
    public List<string> VoiceDo { get; set; } = [];

    /// <summary>"Don't" language guidance: phrasings and moves the voice must avoid.</summary>
    public List<string> VoiceDont { get; set; } = [];

    // --- Visual tokens ----------------------------------------------------
    /// <summary>Brand color palette as hex strings (e.g. "#1A2B3C").</summary>
    public List<string> ColorHexes { get; set; } = [];

    /// <summary>Free-form description of the brand's imagery style.</summary>
    public string ImageryStyle { get; set; } = default!;

    // --- Content pillars --------------------------------------------------
    /// <summary>
    /// The brand's structured content pillars — the validation contract the Content
    /// Strategist's <c>pillar</c> is checked against at receipt (DL-026, DL-034 R7). Set at
    /// onboarding, brand-scoped under the same RLS policy. Distinct from the brand_playbook
    /// prose, which remains generation grounding (the list is the contract, not a replacement).
    /// </summary>
    public List<string> ContentPillars { get; set; } = [];

    // --- Audience ---------------------------------------------------------
    /// <summary>Target audience segments.</summary>
    public List<string> AudienceSegments { get; set; } = [];

    /// <summary>Audience pain points the brand speaks to.</summary>
    public List<string> AudiencePainPoints { get; set; } = [];

    // --- Product context --------------------------------------------------
    /// <summary>What the brand sells and the context an agent needs to ground content.</summary>
    public string ProductContext { get; set; } = default!;

    public DateTimeOffset CreatedAt { get; set; }
}
