using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase9Slice1EvalRunResult : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "eval_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    git_sha = table.Column<string>(type: "text", nullable: false),
                    prompt_version = table.Column<string>(type: "text", nullable: false),
                    model_name = table.Column<string>(type: "text", nullable: false),
                    model_version = table.Column<string>(type: "text", nullable: false),
                    temperature = table.Column<double>(type: "double precision", nullable: false),
                    dataset_name = table.Column<string>(type: "text", nullable: false),
                    dataset_version = table.Column<string>(type: "text", nullable: false),
                    dataset_size = table.Column<int>(type: "integer", nullable: false),
                    aggregates = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_eval_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "eval_results",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<string>(type: "text", nullable: false),
                    evaluator_name = table.Column<string>(type: "text", nullable: false),
                    score = table.Column<double>(type: "double precision", nullable: false),
                    reasoning = table.Column<string>(type: "text", nullable: true),
                    cost_usd = table.Column<decimal>(type: "numeric", nullable: true),
                    latency_ms = table.Column<long>(type: "bigint", nullable: true),
                    metadata = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_eval_results", x => x.id);
                    table.ForeignKey(
                        name: "fk_eval_results_eval_runs_run_id",
                        column: x => x.run_id,
                        principalTable: "eval_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_eval_results_brand_id",
                table: "eval_results",
                column: "brand_id");

            migrationBuilder.CreateIndex(
                name: "ix_eval_results_run_id",
                table: "eval_results",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "ix_eval_runs_brand_id",
                table: "eval_runs",
                column: "brand_id");

            // RLS travels with the table (DL-052): both eval tables are brand-scoped, isolated by the
            // same transaction-local app.current_brand bind as every other tenant table. ENABLE + FORCE
            // so the table owner is subject too; NULLIF maps the post-revert empty-string GUC to NULL
            // (fail-closed → zero rows when no brand is bound).
            foreach (var table in new[] { "eval_runs", "eval_results" })
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
            foreach (var table in new[] { "eval_results", "eval_runs" })
            {
                migrationBuilder.Sql($@"
                    DROP POLICY IF EXISTS {table}_brand_isolation ON {table};
                    ALTER TABLE {table} NO FORCE ROW LEVEL SECURITY;
                    ALTER TABLE {table} DISABLE ROW LEVEL SECURITY;
                ");
            }

            migrationBuilder.DropTable(
                name: "eval_results");

            migrationBuilder.DropTable(
                name: "eval_runs");
        }
    }
}
