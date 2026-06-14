using System.Text;
using Backend.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Backend.Infrastructure.Persistence;

/// <summary>
/// The application's EF Core context. Maps the domain entities to snake_case
/// Postgres tables and columns (so the hand-authored RLS SQL in the migration lines
/// up), stores enums as text, and indexes <c>brand_id</c> on every brand-scoped
/// table. Brand isolation is NOT a query filter here — it is enforced by Postgres
/// RLS plus the transaction-local binding in <c>BrandScope</c> (DL-007).
/// </summary>
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<Brand> Brands => Set<Brand>();

    public DbSet<BrandProfile> BrandProfiles => Set<BrandProfile>();

    public DbSet<KnowledgeDoc> KnowledgeDocs => Set<KnowledgeDoc>();

    public DbSet<KnowledgeChunk> KnowledgeChunks => Set<KnowledgeChunk>();

    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();

    public DbSet<RunCheckpoint> RunCheckpoints => Set<RunCheckpoint>();

    public DbSet<ContentItem> ContentItems => Set<ContentItem>();

    public DbSet<Asset> Assets => Set<Asset>();

    public DbSet<ApprovalAction> ApprovalActions => Set<ApprovalAction>();

    public DbSet<BrandMetaConnection> BrandMetaConnections => Set<BrandMetaConnection>();

    public DbSet<EvalRecord> EvalRecords => Set<EvalRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Enums persist as text, not magic ints, so the database stays self-describing.
        modelBuilder.Entity<AgentRun>().Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
        modelBuilder.Entity<ContentItem>().Property(e => e.Status).HasConversion<string>().HasMaxLength(32);
        modelBuilder.Entity<ApprovalAction>().Property(e => e.Decision).HasConversion<string>().HasMaxLength(16);

        // RAG schema (DL-016, DL-026). The pgvector extension is created by the migration;
        // the generated tsvector column + HNSW/GIN indexes are added via raw migrationBuilder.Sql.
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<KnowledgeDoc>(entity =>
        {
            entity.Property(e => e.DocType).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.Facet).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.Metadata).HasColumnType("jsonb");
        });

        modelBuilder.Entity<KnowledgeChunk>(entity =>
        {
            entity.Property(e => e.DocType).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.Facet).HasConversion<string>().HasMaxLength(16);
            entity.Property(e => e.Embedding).HasColumnType($"vector({KnowledgeChunk.EmbeddingDimension})");
            entity.Property(e => e.Metadata).HasColumnType("jsonb");
            // search_vector is a generated column added by raw SQL in the migration and
            // intentionally NOT mapped here (slice 2). It self-maintains; slice 3 maps it.
        });

        // Every brand-scoped table gets an index on its RLS predicate column.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (typeof(IBrandScoped).IsAssignableFrom(entityType.ClrType))
            {
                modelBuilder.Entity(entityType.ClrType).HasIndex(nameof(IBrandScoped.BrandId));
            }
        }

        // snake_case every table/column/key/fk/index, and make Guid primary keys
        // app-assigned (no DB default, no sequence — keeps grants minimal under RLS).
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            if (tableName is not null)
            {
                entityType.SetTableName(ToSnakeCase(tableName));
            }

            foreach (var property in entityType.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }

            foreach (var key in entityType.GetKeys())
            {
                var keyName = key.GetName();
                if (keyName is not null)
                {
                    key.SetName(ToSnakeCase(keyName));
                }
            }

            foreach (var foreignKey in entityType.GetForeignKeys())
            {
                var constraintName = foreignKey.GetConstraintName();
                if (constraintName is not null)
                {
                    foreignKey.SetConstraintName(ToSnakeCase(constraintName));
                }
            }

            foreach (var index in entityType.GetIndexes())
            {
                var indexName = index.GetDatabaseName();
                if (indexName is not null)
                {
                    index.SetDatabaseName(ToSnakeCase(indexName));
                }
            }

            var primaryKey = entityType.FindPrimaryKey();
            if (primaryKey is not null)
            {
                foreach (var property in primaryKey.Properties)
                {
                    if (property.ClrType == typeof(Guid))
                    {
                        property.ValueGenerated = ValueGenerated.Never;
                    }
                }
            }
        }
    }

    /// <summary>Converts a PascalCase/identifier name to snake_case for Postgres.</summary>
    private static string ToSnakeCase(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var current = name[i];

            if (current == '_')
            {
                if (builder.Length > 0 && builder[^1] != '_')
                {
                    builder.Append('_');
                }

                continue;
            }

            if (char.IsUpper(current))
            {
                var atBoundary = i > 0
                    && (char.IsLower(name[i - 1])
                        || char.IsDigit(name[i - 1])
                        || (i + 1 < name.Length && char.IsLower(name[i + 1])));

                if (atBoundary && builder.Length > 0 && builder[^1] != '_')
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(current));
            }
            else
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }
}
