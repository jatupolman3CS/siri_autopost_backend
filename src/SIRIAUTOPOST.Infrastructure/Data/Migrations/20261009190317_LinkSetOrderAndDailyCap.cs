using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIRIAUTOPOST.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class LinkSetOrderAndDailyCap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "daily_per_group",
                table: "LINK_SETS",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "random_order",
                table: "LINK_SETS",
                type: "boolean",
                nullable: false,
                defaultValue: true); // every set that exists already keeps its groups in a random order, as new ones do
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "daily_per_group",
                table: "LINK_SETS");

            migrationBuilder.DropColumn(
                name: "random_order",
                table: "LINK_SETS");
        }
    }
}
