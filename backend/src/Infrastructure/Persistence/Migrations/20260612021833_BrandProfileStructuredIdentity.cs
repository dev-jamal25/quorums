using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backend.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BrandProfileStructuredIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "brief",
                table: "brand_profiles",
                newName: "product_context");

            migrationBuilder.RenameColumn(
                name: "brand_voice",
                table: "brand_profiles",
                newName: "positioning");

            migrationBuilder.AddColumn<List<string>>(
                name: "audience_pain_points",
                table: "brand_profiles",
                type: "text[]",
                nullable: false);

            migrationBuilder.AddColumn<List<string>>(
                name: "audience_segments",
                table: "brand_profiles",
                type: "text[]",
                nullable: false);

            migrationBuilder.AddColumn<List<string>>(
                name: "color_hexes",
                table: "brand_profiles",
                type: "text[]",
                nullable: false);

            migrationBuilder.AddColumn<string>(
                name: "imagery_style",
                table: "brand_profiles",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<List<string>>(
                name: "tone_descriptors",
                table: "brand_profiles",
                type: "text[]",
                nullable: false);

            migrationBuilder.AddColumn<List<string>>(
                name: "voice_do",
                table: "brand_profiles",
                type: "text[]",
                nullable: false);

            migrationBuilder.AddColumn<List<string>>(
                name: "voice_dont",
                table: "brand_profiles",
                type: "text[]",
                nullable: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "audience_pain_points",
                table: "brand_profiles");

            migrationBuilder.DropColumn(
                name: "audience_segments",
                table: "brand_profiles");

            migrationBuilder.DropColumn(
                name: "color_hexes",
                table: "brand_profiles");

            migrationBuilder.DropColumn(
                name: "imagery_style",
                table: "brand_profiles");

            migrationBuilder.DropColumn(
                name: "tone_descriptors",
                table: "brand_profiles");

            migrationBuilder.DropColumn(
                name: "voice_do",
                table: "brand_profiles");

            migrationBuilder.DropColumn(
                name: "voice_dont",
                table: "brand_profiles");

            migrationBuilder.RenameColumn(
                name: "product_context",
                table: "brand_profiles",
                newName: "brief");

            migrationBuilder.RenameColumn(
                name: "positioning",
                table: "brand_profiles",
                newName: "brand_voice");
        }
    }
}
