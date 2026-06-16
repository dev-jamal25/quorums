namespace Backend.Core.Generation;

/// <summary>
/// The structured-output seam (DL-028, DL-034 R4): every agent's typed contract is produced by a
/// forced tool whose input schema is generated from the record. The implementation validates the
/// deserialized output on receipt, retries up to <see cref="StructuredGenerationRequest{T}.MaxRetries"/>
/// times feeding the specific error back, then returns a <c>ToolError</c> — it never throws into the
/// graph (DL-022). The concrete Anthropic/Microsoft.Extensions.AI client stays in Infrastructure
/// (DL-032); consumers depend only on this abstraction.
/// </summary>
public interface IStructuredGenerator
{
    Task<GenerationOutcome<T>> GenerateAsync<T>(
        StructuredGenerationRequest<T> request,
        CancellationToken cancellationToken = default)
        where T : class;
}
