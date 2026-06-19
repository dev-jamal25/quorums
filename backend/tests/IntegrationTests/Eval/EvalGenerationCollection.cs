using Xunit;

namespace Backend.IntegrationTests.Eval;

/// <summary>
/// Serializes the Eval tests that run a real in-process MAF generation. The MAF workflow runner is not
/// safe to run highly concurrently (under load it can fail to capture the terminal node's output); the
/// repo already serializes its other MAF-running suites via collections (Durability/Knowledge). No shared
/// fixture — this collection exists only to bound concurrency.
/// </summary>
[CollectionDefinition("EvalGeneration")]
public sealed class EvalGenerationCollectionDefinition;
