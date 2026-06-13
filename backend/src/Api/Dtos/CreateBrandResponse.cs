namespace Backend.Api.Dtos;

/// <summary>The <c>POST /brands</c> response: the id of the newly onboarded brand.</summary>
public sealed record CreateBrandResponse(Guid Id);
