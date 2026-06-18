namespace Backend.Api.Dtos;

/// <summary>Cancel a scheduled run (DL-037). Valid only when the run is <c>Scheduled</c>; reason optional.</summary>
public sealed record CancelRequest(string? Reason);
