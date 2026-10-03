using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIRIAUTOPOST.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Devices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_posts_account_id",
                table: "posts");

            migrationBuilder.AddColumn<Guid>(
                name: "device_id",
                table: "social_accounts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "group_links",
                table: "social_accounts",
                type: "jsonb",
                nullable: true,
                defaultValue: "[]"); // existing accounts start with no group links

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "claimed_at",
                table: "posts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "claimed_by_device_id",
                table: "posts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "failure_detail",
                table: "posts",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "device_pairings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_pairings", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_pairings_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    browser = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_devices", x => x.id);
                    table.ForeignKey(
                        name: "fk_devices_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_social_accounts_device_id",
                table: "social_accounts",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_posts_account_id_status_scheduled_at",
                table: "posts",
                columns: new[] { "account_id", "status", "scheduled_at" });

            migrationBuilder.CreateIndex(
                name: "ix_posts_claimed_by_device_id",
                table: "posts",
                column: "claimed_by_device_id");

            migrationBuilder.CreateIndex(
                name: "ix_device_pairings_code",
                table: "device_pairings",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_pairings_workspace_id",
                table: "device_pairings",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_devices_key_hash",
                table: "devices",
                column: "key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_devices_workspace_id",
                table: "devices",
                column: "workspace_id");

            migrationBuilder.AddForeignKey(
                name: "fk_posts_devices_claimed_by_device_id",
                table: "posts",
                column: "claimed_by_device_id",
                principalTable: "devices",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_social_accounts_devices_device_id",
                table: "social_accounts",
                column: "device_id",
                principalTable: "devices",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_posts_devices_claimed_by_device_id",
                table: "posts");

            migrationBuilder.DropForeignKey(
                name: "fk_social_accounts_devices_device_id",
                table: "social_accounts");

            migrationBuilder.DropTable(
                name: "device_pairings");

            migrationBuilder.DropTable(
                name: "devices");

            migrationBuilder.DropIndex(
                name: "ix_social_accounts_device_id",
                table: "social_accounts");

            migrationBuilder.DropIndex(
                name: "ix_posts_account_id_status_scheduled_at",
                table: "posts");

            migrationBuilder.DropIndex(
                name: "ix_posts_claimed_by_device_id",
                table: "posts");

            migrationBuilder.DropColumn(
                name: "device_id",
                table: "social_accounts");

            migrationBuilder.DropColumn(
                name: "group_links",
                table: "social_accounts");

            migrationBuilder.DropColumn(
                name: "claimed_at",
                table: "posts");

            migrationBuilder.DropColumn(
                name: "claimed_by_device_id",
                table: "posts");

            migrationBuilder.DropColumn(
                name: "failure_detail",
                table: "posts");

            migrationBuilder.CreateIndex(
                name: "ix_posts_account_id",
                table: "posts",
                column: "account_id");
        }
    }
}
