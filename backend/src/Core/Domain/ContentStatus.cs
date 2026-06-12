namespace Backend.Core.Domain;

/// <summary>Status of a generated <see cref="ContentItem"/> through the human gate.</summary>
public enum ContentStatus
{
    Draft,
    AwaitingApproval,
    Approved,
    Rejected,
    Published,
}
