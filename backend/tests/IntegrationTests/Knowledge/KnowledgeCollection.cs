using Xunit;

namespace Backend.IntegrationTests.Knowledge;

/// <summary>
/// Shares one <see cref="KnowledgeFixture"/> (a single pgvector container + one seeding) across
/// every RAG test class, instead of one container per class. xUnit runs the classes in this
/// collection sequentially, so the mutating tests (which use their own random-id docs) never
/// race the read-only proofs, and the suite spins up far fewer simultaneous containers.
/// </summary>
[CollectionDefinition("Knowledge")]
public sealed class KnowledgeCollectionDefinition : ICollectionFixture<KnowledgeFixture>;
