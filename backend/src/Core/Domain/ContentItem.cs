namespace Backend.Core.Domain;

/// <summary>A generated caption plus its asset references and gate status (DL-005).</summary>
public sealed class ContentItem : IBrandScoped
{
    public Guid Id { get; set; }

    public Guid BrandId { get; set; }

    public Guid AgentRunId { get; set; }

    public string Caption { get; set; } = default!;

    public ContentStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
