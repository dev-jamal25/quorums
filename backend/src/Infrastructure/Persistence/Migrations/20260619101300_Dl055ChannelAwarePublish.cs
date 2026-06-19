using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Dl055ChannelAwarePublish : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_publish_records_content_item_id",
                table: "publish_records");

            // Pre-DL-055 publishing was Instagram-only, so existing rows backfill to Instagram; this
            // also makes the new (content_item_id, channel) unique index apply cleanly. RLS is unchanged
            // (a column add to an existing brand-scoped table — no new table, no new policy).
            migrationBuilder.AddColumn<string>(
                name: "channel",
                table: "publish_records",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Instagram");

            // Composite UNIQUE idempotency index — one row per (content_item_id, channel), DL-055.
            // Shipped as SQL (same idiom as the RLS policy this table carries) for an explicit name.
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX ix_publish_records_content_item_id_channel
                    ON publish_records (content_item_id, channel);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_publish_records_content_item_id_channel;");

            migrationBuilder.DropColumn(
                name: "channel",
                table: "publish_records");

            migrationBuilder.CreateIndex(
                name: "ix_publish_records_content_item_id",
                table: "publish_records",
                column: "content_item_id");
        }
    }
}
