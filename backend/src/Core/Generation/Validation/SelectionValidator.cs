namespace Backend.Core.Generation.Validation;

/// <summary>
/// Validates the Supervisor's <c>SelectionDecision.ChosenIndex</c> against the candidate count
/// (DL-034 R5): it must be in <c>[0, N)</c>. Out of range is a schema violation that drives the
/// bounded retry then a <c>ToolError</c>. Pure and post-deserialization — the forced tool does not
/// cover it.
/// </summary>
public static class SelectionValidator
{
    public static ValidationResult Validate(int chosenIndex, int candidateCount)
    {
        if (candidateCount <= 0)
        {
            return ValidationResult.Invalid("no candidates were produced to select from");
        }

        return chosenIndex >= 0 && chosenIndex < candidateCount
            ? ValidationResult.Valid
            : ValidationResult.Invalid(
                $"chosenIndex {chosenIndex} is out of range [0, {candidateCount})");
    }
}
