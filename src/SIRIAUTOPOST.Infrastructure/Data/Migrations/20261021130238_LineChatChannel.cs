using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIRIAUTOPOST.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class LineChatChannel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "rest_until",
                table: "SOCIAL_ACCOUNTS",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "channel",
                table: "SET_LINKS",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Fb");

            migrationBuilder.AddColumn<string>(
                name: "line_options",
                table: "SCHEDULES",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "defer_count",
                table: "POSTS",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "channel",
                table: "LINK_SETS",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Fb");

            migrationBuilder.AddColumn<string>(
                name: "line_bot_id",
                table: "DEVICES",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "line_ready_at",
                table: "DEVICES",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LINE_CHATS",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    chat_url = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    labels = table.Column<string>(type: "jsonb", nullable: false),
                    last_message_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    unread = table.Column<bool>(type: "boolean", nullable: false),
                    do_not_send = table.Column<bool>(type: "boolean", nullable: false),
                    synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_line_chats", x => x.id);
                    table.ForeignKey(
                        name: "fk_line_chats_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "SOCIAL_ACCOUNTS",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_line_chats_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "WORKSPACES",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_social_accounts_workspace_id_platform_handle",
                table: "SOCIAL_ACCOUNTS",
                columns: new[] { "workspace_id", "platform", "handle" },
                unique: true,
                filter: "platform = 'Line'");

            migrationBuilder.CreateIndex(
                name: "ix_line_chats_account_id_user_id",
                table: "LINE_CHATS",
                columns: new[] { "account_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_line_chats_workspace_id_account_id_name",
                table: "LINE_CHATS",
                columns: new[] { "workspace_id", "account_id", "name" });

            migrationBuilder.Sql(
                """
                UPDATE "WORKSPACES"
                SET anti_ban = jsonb_set(anti_ban, '{LineChat}',
                    '{"MinGapSeconds": 45, "MaxGapSeconds": 120}'::jsonb,
                    true)
                WHERE NOT (anti_ban ? 'LineChat');

                -- Old rows may hold a stale "Line": 3 (the limits of a posting platform that was never built; nothing read
                -- it). The key means "LINE chats per 24 hours" now, so EVERY row gets the default, present or not.
                UPDATE "WORKSPACES"
                SET anti_ban = jsonb_set(anti_ban, '{Limits,Line}', '100'::jsonb, true)
                WHERE anti_ban ? 'Limits';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "WORKSPACES"
                SET anti_ban = (anti_ban - 'LineChat') #- '{Limits,Line}';
                """);

            migrationBuilder.DropTable(
                name: "LINE_CHATS");

            migrationBuilder.DropIndex(
                name: "ix_social_accounts_workspace_id_platform_handle",
                table: "SOCIAL_ACCOUNTS");

            migrationBuilder.DropColumn(
                name: "rest_until",
                table: "SOCIAL_ACCOUNTS");

            migrationBuilder.DropColumn(
                name: "channel",
                table: "SET_LINKS");

            migrationBuilder.DropColumn(
                name: "line_options",
                table: "SCHEDULES");

            migrationBuilder.DropColumn(
                name: "defer_count",
                table: "POSTS");

            migrationBuilder.DropColumn(
                name: "channel",
                table: "LINK_SETS");

            migrationBuilder.DropColumn(
                name: "line_bot_id",
                table: "DEVICES");

            migrationBuilder.DropColumn(
                name: "line_ready_at",
                table: "DEVICES");
        }
    }
}
