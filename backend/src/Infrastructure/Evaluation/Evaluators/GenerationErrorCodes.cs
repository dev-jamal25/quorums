namespace Backend.Infrastructure.Evaluation.Evaluators;

/// <summary>The <see cref="Backend.Core.Orchestration.ToolError"/> codes the rule-based evaluators key off.</summary>
internal static class GenerationErrorCodes
{
    /// <summary>Emitted by <c>ForcedToolGenerator</c> when structured output is invalid after the bounded retries.</summary>
    public const string SchemaViolation = "generation.schema_violation";
}
