using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Dl058RunModalitySelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill existing rows to Image (DL-058) so old runs load + behave exactly as before; new
            // rows always set modality explicitly at POST /runs. Column-add only — the agent_runs RLS
            // policy is untouched (it travels with the table, created in InitialCreate).
            migrationBuilder.AddColumn<string>(
                name: "modality",
                table: "agent_runs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Image");

            migrationBuilder.AddColumn<string>(
                name: "video_source",
                table: "agent_runs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "modality",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "video_source",
                table: "agent_runs");
        }
    }
}
