namespace Backend.Core.Domain;

/// <summary>A manager-editable brand-knowledge document (the CMS corpus, DL-010).</summary>
public sealed class KnowledgeDoc : IBrandScoped
{
    public Guid Id { get; set; }

    public Guid BrandId { get; set; }

    public string Title { get; set; } = default!;

    public string Content { get; set; } = default!;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
