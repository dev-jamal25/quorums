using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Phase6Slice1ApprovalPublishAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "decision",
                table: "approval_actions");

            migrationBuilder.RenameColumn(
                name: "decided_by",
                table: "approval_actions",
                newName: "actor");

            migrationBuilder.RenameColumn(
                name: "decided_at",
                table: "approval_actions",
                newName: "occurred_at");

            migrationBuilder.AddColumn<string>(
                name: "action",
                table: "approval_actions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "edited_caption",
                table: "approval_actions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "edited_hashtags",
                table: "approval_actions",
                type: "text[]",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reason",
                table: "approval_actions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "regenerate_mode",
                table: "approval_actions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "scheduled_for",
                table: "approval_actions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "publish_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    external_ref = table.Column<string>(type: "text", nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    engagement_keys = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_publish_records", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_publish_records_brand_id",
                table: "publish_records",
                column: "brand_id");

            migrationBuilder.CreateIndex(
                name: "ix_publish_records_content_item_id",
                table: "publish_records",
                column: "content_item_id");

            // --- Row-Level Security for the new brand-scoped table (DL-002, DL-007) ----------
            // approval_actions already carries its policy from InitialCreate (its columns changed
            // here, not its table-level RLS), so ONLY publish_records is new. RLS does not travel
            // automatically — enable it here, same ENABLE + FORCE + brand-GUC policy as every other
            // brand-scoped table. The NULLIF(...,'')::uuid predicate fails closed (zero rows) when
            // app.current_brand is unset or reverted, exactly as in InitialCreate.
            migrationBuilder.Sql(@"
                ALTER TABLE publish_records ENABLE ROW LEVEL SECURITY;
                ALTER TABLE publish_records FORCE ROW LEVEL SECURITY;
                CREATE POLICY publish_records_brand_isolation ON publish_records
                    USING (brand_id = NULLIF(current_setting('app.current_brand', true), '')::uuid)
                    WITH CHECK (brand_id = NULLIF(current_setting('app.current_brand', true), '')::uuid);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse the RLS before the table goes (DropTable would cascade it, but explicit keeps
            // the migration symmetric and re-runnable — matching InitialCreate).
            migrationBuilder.Sql(@"
                DROP POLICY IF EXISTS publish_records_brand_isolation ON publish_records;
                ALTER TABLE publish_records NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE publish_records DISABLE ROW LEVEL SECURITY;
            ");

            migrationBuilder.DropTable(
                name: "publish_records");

            migrationBuilder.DropColumn(
                name: "action",
                table: "approval_actions");

            migrationBuilder.DropColumn(
                name: "edited_caption",
                table: "approval_actions");

            migrationBuilder.DropColumn(
                name: "edited_hashtags",
                table: "approval_actions");

            migrationBuilder.DropColumn(
                name: "reason",
                table: "approval_actions");

            migrationBuilder.DropColumn(
                name: "regenerate_mode",
                table: "approval_actions");

            migrationBuilder.DropColumn(
                name: "scheduled_for",
                table: "approval_actions");

            migrationBuilder.RenameColumn(
                name: "occurred_at",
                table: "approval_actions",
                newName: "decided_at");

            migrationBuilder.RenameColumn(
                name: "actor",
                table: "approval_actions",
                newName: "decided_by");

            migrationBuilder.AddColumn<string>(
                name: "decision",
                table: "approval_actions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");
        }
    }
}
