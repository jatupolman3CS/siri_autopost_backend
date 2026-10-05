using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIRIAUTOPOST.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ActiveAndRename : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "active",
                table: "SNIPPETS",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "active",
                table: "POST_COLLECTIONS",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "active",
                table: "MEDIA_FILES",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "active",
                table: "LINK_SETS",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "active",
                table: "SNIPPETS");

            migrationBuilder.DropColumn(
                name: "active",
                table: "POST_COLLECTIONS");

            migrationBuilder.DropColumn(
                name: "active",
                table: "MEDIA_FILES");

            migrationBuilder.DropColumn(
                name: "active",
                table: "LINK_SETS");
        }
    }
}
