using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace Backend.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KnowledgeVectorSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.AddColumn<string>(
                name: "doc_type",
                table: "knowledge_docs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "facet",
                table: "knowledge_docs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "metadata",
                table: "knowledge_docs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "knowledge_docs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "doc_type",
                table: "knowledge_chunks",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Vector>(
                name: "embedding",
                table: "knowledge_chunks",
                type: "vector(768)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "facet",
                table: "knowledge_chunks",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "metadata",
                table: "knowledge_chunks",
                type: "jsonb",
                nullable: true);

            // --- Raw SQL EF cannot express (DL-016, DL-025) -------------------------
            // Generated, self-maintaining sparse-search column — slice 3 FTS queries it;
            // slice 2 only populates it. Lives on the snake_case `content` column.
            migrationBuilder.Sql(
                "ALTER TABLE knowledge_chunks ADD COLUMN search_vector tsvector " +
                "GENERATED ALWAYS AS (to_tsvector('english', content)) STORED;");

            // Sparse arm index (slice 3).
            migrationBuilder.Sql(
                "CREATE INDEX ix_knowledge_chunks_search_vector ON knowledge_chunks USING gin (search_vector);");

            // Dense arm index — cosine HNSW (EF cannot express USING hnsw).
            migrationBuilder.Sql(
                "CREATE INDEX ix_knowledge_chunks_embedding ON knowledge_chunks USING hnsw (embedding vector_cosine_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_knowledge_chunks_embedding;");
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_knowledge_chunks_search_vector;");
            migrationBuilder.Sql("ALTER TABLE knowledge_chunks DROP COLUMN IF EXISTS search_vector;");

            migrationBuilder.DropColumn(
                name: "doc_type",
                table: "knowledge_docs");

            migrationBuilder.DropColumn(
                name: "facet",
                table: "knowledge_docs");

            migrationBuilder.DropColumn(
                name: "metadata",
                table: "knowledge_docs");

            migrationBuilder.DropColumn(
                name: "source",
                table: "knowledge_docs");

            migrationBuilder.DropColumn(
                name: "doc_type",
                table: "knowledge_chunks");

            migrationBuilder.DropColumn(
                name: "embedding",
                table: "knowledge_chunks");

            migrationBuilder.DropColumn(
                name: "facet",
                table: "knowledge_chunks");

            migrationBuilder.DropColumn(
                name: "metadata",
                table: "knowledge_chunks");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
        }
    }
}
