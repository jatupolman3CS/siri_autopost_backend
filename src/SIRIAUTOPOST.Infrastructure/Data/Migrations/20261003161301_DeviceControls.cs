using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIRIAUTOPOST.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class DeviceControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "jobs_paused",
                table: "devices",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "jobs_paused",
                table: "devices");
        }
    }
}
