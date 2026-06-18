using Xunit;

namespace Backend.IntegrationTests.Durability;

/// <summary>
/// Shares ONE <see cref="DurabilityFixture"/> (a single Testcontainers Postgres) across every class
/// that needs the durable-run database, so the suite spins up one container for the group instead of
/// one per class — avoiding container-startup contention under parallel test collections. Tests key
/// off unique run/content ids, so the shared database is safe. Same pattern as the Knowledge collection.
/// </summary>
[CollectionDefinition("Durability")]
public sealed class DurabilityCollectionDefinition : ICollectionFixture<DurabilityFixture>;
