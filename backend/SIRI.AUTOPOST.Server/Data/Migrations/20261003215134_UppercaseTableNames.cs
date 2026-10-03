using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIRI.AUTOPOST.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class UppercaseTableNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "fbap_users",
                newName: "FBAP_USERS");

            migrationBuilder.RenameTable(
                name: "fbap_profiles",
                newName: "FBAP_PROFILES");

            migrationBuilder.RenameTable(
                name: "fbap_profile_images",
                newName: "FBAP_PROFILE_IMAGES");

            migrationBuilder.RenameTable(
                name: "fbap_devices",
                newName: "FBAP_DEVICES");

            migrationBuilder.RenameTable(
                name: "fbap_device_logs",
                newName: "FBAP_DEVICE_LOGS");

            migrationBuilder.RenameTable(
                name: "fbap_device_commands",
                newName: "FBAP_DEVICE_COMMANDS");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "FBAP_USERS",
                newName: "fbap_users");

            migrationBuilder.RenameTable(
                name: "FBAP_PROFILES",
                newName: "fbap_profiles");

            migrationBuilder.RenameTable(
                name: "FBAP_PROFILE_IMAGES",
                newName: "fbap_profile_images");

            migrationBuilder.RenameTable(
                name: "FBAP_DEVICES",
                newName: "fbap_devices");

            migrationBuilder.RenameTable(
                name: "FBAP_DEVICE_LOGS",
                newName: "fbap_device_logs");

            migrationBuilder.RenameTable(
                name: "FBAP_DEVICE_COMMANDS",
                newName: "fbap_device_commands");
        }
    }
}
