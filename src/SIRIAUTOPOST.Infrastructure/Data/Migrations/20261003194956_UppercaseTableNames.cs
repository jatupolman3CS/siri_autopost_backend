using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIRIAUTOPOST.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class UppercaseTableNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "workspaces",
                newName: "WORKSPACES");

            migrationBuilder.RenameTable(
                name: "workspace_members",
                newName: "WORKSPACE_MEMBERS");

            migrationBuilder.RenameTable(
                name: "users",
                newName: "USERS");

            migrationBuilder.RenameTable(
                name: "transactions",
                newName: "TRANSACTIONS");

            migrationBuilder.RenameTable(
                name: "social_accounts",
                newName: "SOCIAL_ACCOUNTS");

            migrationBuilder.RenameTable(
                name: "snippets",
                newName: "SNIPPETS");

            migrationBuilder.RenameTable(
                name: "promos",
                newName: "PROMOS");

            migrationBuilder.RenameTable(
                name: "posts",
                newName: "POSTS");

            migrationBuilder.RenameTable(
                name: "plan_settings",
                newName: "PLAN_SETTINGS");

            migrationBuilder.RenameTable(
                name: "media_files",
                newName: "MEDIA_FILES");

            migrationBuilder.RenameTable(
                name: "extension_images",
                newName: "EXTENSION_IMAGES");

            migrationBuilder.RenameTable(
                name: "extension_configs",
                newName: "EXTENSION_CONFIGS");

            migrationBuilder.RenameTable(
                name: "devices",
                newName: "DEVICES");

            migrationBuilder.RenameTable(
                name: "device_states",
                newName: "DEVICE_STATES");

            migrationBuilder.RenameTable(
                name: "device_pairings",
                newName: "DEVICE_PAIRINGS");

            migrationBuilder.RenameTable(
                name: "device_logs",
                newName: "DEVICE_LOGS");

            migrationBuilder.RenameTable(
                name: "device_events",
                newName: "DEVICE_EVENTS");

            migrationBuilder.RenameTable(
                name: "device_commands",
                newName: "DEVICE_COMMANDS");

            migrationBuilder.RenameTable(
                name: "audit_entries",
                newName: "AUDIT_ENTRIES");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "WORKSPACES",
                newName: "workspaces");

            migrationBuilder.RenameTable(
                name: "WORKSPACE_MEMBERS",
                newName: "workspace_members");

            migrationBuilder.RenameTable(
                name: "USERS",
                newName: "users");

            migrationBuilder.RenameTable(
                name: "TRANSACTIONS",
                newName: "transactions");

            migrationBuilder.RenameTable(
                name: "SOCIAL_ACCOUNTS",
                newName: "social_accounts");

            migrationBuilder.RenameTable(
                name: "SNIPPETS",
                newName: "snippets");

            migrationBuilder.RenameTable(
                name: "PROMOS",
                newName: "promos");

            migrationBuilder.RenameTable(
                name: "POSTS",
                newName: "posts");

            migrationBuilder.RenameTable(
                name: "PLAN_SETTINGS",
                newName: "plan_settings");

            migrationBuilder.RenameTable(
                name: "MEDIA_FILES",
                newName: "media_files");

            migrationBuilder.RenameTable(
                name: "EXTENSION_IMAGES",
                newName: "extension_images");

            migrationBuilder.RenameTable(
                name: "EXTENSION_CONFIGS",
                newName: "extension_configs");

            migrationBuilder.RenameTable(
                name: "DEVICES",
                newName: "devices");

            migrationBuilder.RenameTable(
                name: "DEVICE_STATES",
                newName: "device_states");

            migrationBuilder.RenameTable(
                name: "DEVICE_PAIRINGS",
                newName: "device_pairings");

            migrationBuilder.RenameTable(
                name: "DEVICE_LOGS",
                newName: "device_logs");

            migrationBuilder.RenameTable(
                name: "DEVICE_EVENTS",
                newName: "device_events");

            migrationBuilder.RenameTable(
                name: "DEVICE_COMMANDS",
                newName: "device_commands");

            migrationBuilder.RenameTable(
                name: "AUDIT_ENTRIES",
                newName: "audit_entries");
        }
    }
}
