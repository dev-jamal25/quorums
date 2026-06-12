using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        // Every domain table except `brands` (the scope itself). Hangfire's tables
        // live in their own schema and are never touched here (DL-002, DL-007).
        private static string[] BrandScopedTables() =>
        [
            "brand_profiles",
            "knowledge_docs",
            "knowledge_chunks",
            "agent_runs",
            "run_checkpoints",
            "content_items",
            "assets",
            "approval_actions",
            "brand_meta_connections",
            "eval_records",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "approval_actions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    decided_by = table.Column<string>(type: "text", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_approval_actions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_key = table.Column<string>(type: "text", nullable: false),
                    content_type = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "brand_meta_connections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_ciphertext = table.Column<string>(type: "text", nullable: false),
                    token_type = table.Column<string>(type: "text", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    scopes = table.Column<string>(type: "text", nullable: true),
                    rotated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_brand_meta_connections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "brand_profiles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    brief = table.Column<string>(type: "text", nullable: false),
                    brand_voice = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_brand_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "brands",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_brands", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "content_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    caption = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_content_items", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "eval_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    metric = table.Column<string>(type: "text", nullable: false),
                    score = table.Column<double>(type: "double precision", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_eval_records", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_chunks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    knowledge_doc_id = table.Column<Guid>(type: "uuid", nullable: false),
                    chunk_index = table.Column<int>(type: "integer", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_knowledge_chunks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "knowledge_docs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_knowledge_docs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "run_checkpoints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state_json = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_run_checkpoints", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_runs_brand_id",
                table: "agent_runs",
                column: "brand_id");

            migrationBuilder.CreateIndex(
                name: "ix_approval_actions_brand_id",
                table: "approval_actions",
                column: "brand_id");

            migrationBuilder.CreateIndex(
                name: "ix_assets_brand_id",
                table: "assets",
                column: "brand_id");

            migrationBuilder.CreateIndex(
                name: "ix_brand_meta_connections_brand_id",
                table: "brand_meta_connections",
                column: "brand_id");

            migrationBuilder.CreateIndex(
                name: "ix_brand_profiles_brand_id",
                table: "brand_profiles",
                column: "brand_id");

            migrationBuilder.CreateIndex(
                name: "ix_content_items_brand_id",
                table: "content_items",
                column: "brand_id");

            migrationBuilder.CreateIndex(
                name: "ix_eval_records_brand_id",
                table: "eval_records",
                column: "brand_id");

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_chunks_brand_id",
                table: "knowledge_chunks",
                column: "brand_id");

            migrationBuilder.CreateIndex(
                name: "ix_knowledge_docs_brand_id",
                table: "knowledge_docs",
                column: "brand_id");

            migrationBuilder.CreateIndex(
                name: "ix_run_checkpoints_brand_id",
                table: "run_checkpoints",
                column: "brand_id");

            // --- Row-Level Security (DL-002, DL-007) ----------------------------
            // Brand isolation is enforced by Postgres RLS, not by application-layer
            // WHERE clauses. For every brand-scoped table: ENABLE + FORCE RLS, plus a
            // policy that admits a row only when its brand_id equals the
            // transaction-local app.current_brand GUC (bound by BrandScope as the
            // first statement of the work transaction).
            //
            // FORCE binds the table owner too, so the policy holds even if the app
            // ever connects as the owner; the app's runtime role is a non-owner,
            // least-privilege role subject to RLS regardless.
            //
            // NULLIF(current_setting('app.current_brand', true), '')::uuid resolves
            // the binding with missing_ok = true and treats "unset" as NULL:
            //   * never bound on this connection            -> current_setting -> NULL
            //   * bound transaction-locally, then reverted   -> a custom GUC reads
            //     back as '' (empty), so NULLIF maps it to NULL
            // Either way the predicate is NULL and the table yields ZERO rows (fail
            // closed) — never an error (''::uuid would throw) and never every row.
            foreach (var table in BrandScopedTables())
            {
                migrationBuilder.Sql($@"
                    ALTER TABLE {table} ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE {table} FORCE ROW LEVEL SECURITY;
                    CREATE POLICY {table}_brand_isolation ON {table}
                        USING (brand_id = NULLIF(current_setting('app.current_brand', true), '')::uuid)
                        WITH CHECK (brand_id = NULLIF(current_setting('app.current_brand', true), '')::uuid);
                ");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop the RLS policies and disable RLS before the tables go (dropping a
            // table would cascade these away anyway; explicit keeps the migration
            // symmetric and re-runnable).
            foreach (var table in BrandScopedTables())
            {
                migrationBuilder.Sql($@"
                    DROP POLICY IF EXISTS {table}_brand_isolation ON {table};
                    ALTER TABLE {table} NO FORCE ROW LEVEL SECURITY;
                    ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;
                ");
            }

            migrationBuilder.DropTable(
                name: "agent_runs");

            migrationBuilder.DropTable(
                name: "approval_actions");

            migrationBuilder.DropTable(
                name: "assets");

            migrationBuilder.DropTable(
                name: "brand_meta_connections");

            migrationBuilder.DropTable(
                name: "brand_profiles");

            migrationBuilder.DropTable(
                name: "brands");

            migrationBuilder.DropTable(
                name: "content_items");

            migrationBuilder.DropTable(
                name: "eval_records");

            migrationBuilder.DropTable(
                name: "knowledge_chunks");

            migrationBuilder.DropTable(
                name: "knowledge_docs");

            migrationBuilder.DropTable(
                name: "run_checkpoints");
        }
    }
}
