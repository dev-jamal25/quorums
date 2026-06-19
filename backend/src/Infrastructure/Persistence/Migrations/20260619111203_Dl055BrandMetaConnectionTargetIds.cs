using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Dl055BrandMetaConnectionTargetIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Non-secret per-channel target ids (DL-055): the Facebook Page id and IG Business Account id
            // the live publish posts to. Nullable (a brand connects zero/one/both channels). RLS is
            // unchanged — a column add to the already-brand-scoped brand_meta_connections table, not a
            // new table, so its existing policy still applies.
            migrationBuilder.AddColumn<string>(
                name: "facebook_page_id",
                table: "brand_meta_connections",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ig_business_account_id",
                table: "brand_meta_connections",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "facebook_page_id",
                table: "brand_meta_connections");

            migrationBuilder.DropColumn(
                name: "ig_business_account_id",
                table: "brand_meta_connections");
        }
    }
}
