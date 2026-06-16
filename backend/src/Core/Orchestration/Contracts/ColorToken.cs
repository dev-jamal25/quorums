namespace Backend.Core.Orchestration.Contracts;

/// <summary>A named brand colour on a <see cref="CreativeDirection"/> (DL-028): a label plus its hex.</summary>
public sealed record ColorToken(string Name, string Hex);
