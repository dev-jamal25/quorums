namespace Backend.Core.Domain;

/// <summary>Onboarding output for a brand: the brief and the derived brand voice.</summary>
public sealed class BrandProfile : IBrandScoped
{
    public Guid Id { get; set; }

    public Guid BrandId { get; set; }

    public string Brief { get; set; } = default!;

    public string BrandVoice { get; set; } = default!;

    public DateTimeOffset CreatedAt { get; set; }
}
