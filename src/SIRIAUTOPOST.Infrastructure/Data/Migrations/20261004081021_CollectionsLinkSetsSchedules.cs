using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIRIAUTOPOST.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class CollectionsLinkSetsSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "auto_reply",
                table: "WORKSPACES",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "notifications",
                table: "WORKSPACES",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "code",
                table: "POSTS",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "collection_post_id",
                table: "POSTS",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_test",
                table: "POSTS",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "link_id",
                table: "POSTS",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "schedule_id",
                table: "POSTS",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "slot_key",
                table: "POSTS",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "target_key",
                table: "POSTS",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "target_url",
                table: "POSTS",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "auto_pause_reason",
                table: "DEVICES",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "auto_paused_until",
                table: "DEVICES",
                type: "timestamp with time zone",
                nullable: true);

            // The advanced anti-ban numbers live inside the existing anti_ban json: give old rows the defaults.
            migrationBuilder.Sql(
                """
                UPDATE "WORKSPACES"
                SET anti_ban = jsonb_set(anti_ban, '{Advanced}',
                    '{"MinGap": 2, "DailyAll": 0, "BlockMin": 24, "BlockMax": 48, "FailStreak": 4, "RecentAvoid": 10, "Cooldown": 0, "Focus": true, "AutoOffFails": 3, "StopFailPct": 30}'::jsonb,
                    true)
                WHERE NOT (anti_ban ? 'Advanced');
                """);

            migrationBuilder.CreateTable(
                name: "LINK_SETS",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    post_as_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    account_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_link_sets", x => x.id);
                    table.ForeignKey(
                        name: "fk_link_sets_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "WORKSPACES",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "POST_COLLECTIONS",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    icon = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    settings = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_post_collections", x => x.id);
                    table.ForeignKey(
                        name: "fk_post_collections_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "WORKSPACES",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "REPORT_SHARES",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token = table.Column<string>(type: "character varying(43)", maxLength: 43, nullable: false),
                    brand = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    period = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    show_logo = table.Column<bool>(type: "boolean", nullable: false),
                    snapshot_json = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_report_shares", x => x.id);
                    table.ForeignKey(
                        name: "fk_report_shares_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "WORKSPACES",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SET_LINKS",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    link_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    url = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    daily_max = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    health = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    fail_streak = table.Column<int>(type: "integer", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_set_links", x => x.id);
                    table.ForeignKey(
                        name: "fk_set_links_link_sets_link_set_id",
                        column: x => x.link_set_id,
                        principalTable: "LINK_SETS",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_set_links_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "WORKSPACES",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "COLLECTION_POSTS",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    collection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    media_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    approval = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_collection_posts", x => x.id);
                    table.ForeignKey(
                        name: "fk_collection_posts_collections_collection_id",
                        column: x => x.collection_id,
                        principalTable: "POST_COLLECTIONS",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_collection_posts_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "WORKSPACES",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SCHEDULES",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    collection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    link_set_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    times = table.Column<List<string>>(type: "text[]", nullable: false),
                    every_hours = table.Column<int>(type: "integer", nullable: false),
                    first_time = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    once_time = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    order = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    drip_from = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    drip_to = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: false),
                    drip_count = table.Column<int>(type: "integer", nullable: false),
                    bump_hours = table.Column<int>(type: "integer", nullable: false),
                    auto_delete_days = table.Column<int>(type: "integer", nullable: false),
                    overrides = table.Column<string>(type: "jsonb", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    utc_offset_minutes = table.Column<int>(type: "integer", nullable: false),
                    generated_through = table.Column<DateOnly>(type: "date", nullable: true),
                    cursor = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_schedules", x => x.id);
                    table.ForeignKey(
                        name: "fk_schedules_link_sets_link_set_id",
                        column: x => x.link_set_id,
                        principalTable: "LINK_SETS",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_schedules_post_collections_collection_id",
                        column: x => x.collection_id,
                        principalTable: "POST_COLLECTIONS",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "fk_schedules_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "WORKSPACES",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_posts_link_id_scheduled_at",
                table: "POSTS",
                columns: new[] { "link_id", "scheduled_at" },
                filter: "link_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_posts_schedule_id_status_scheduled_at",
                table: "POSTS",
                columns: new[] { "schedule_id", "status", "scheduled_at" },
                filter: "schedule_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_posts_schedule_id_target_key_slot_key",
                table: "POSTS",
                columns: new[] { "schedule_id", "target_key", "slot_key" },
                unique: true,
                filter: "schedule_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_collection_posts_collection_id_created_at",
                table: "COLLECTION_POSTS",
                columns: new[] { "collection_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_collection_posts_workspace_id",
                table: "COLLECTION_POSTS",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_link_sets_workspace_id_sort_order",
                table: "LINK_SETS",
                columns: new[] { "workspace_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_post_collections_workspace_id_sort_order",
                table: "POST_COLLECTIONS",
                columns: new[] { "workspace_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_report_shares_expires_at",
                table: "REPORT_SHARES",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_report_shares_token",
                table: "REPORT_SHARES",
                column: "token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_report_shares_workspace_id",
                table: "REPORT_SHARES",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_schedules_collection_id",
                table: "SCHEDULES",
                column: "collection_id");

            migrationBuilder.CreateIndex(
                name: "ix_schedules_link_set_id",
                table: "SCHEDULES",
                column: "link_set_id");

            migrationBuilder.CreateIndex(
                name: "ix_schedules_workspace_id_active",
                table: "SCHEDULES",
                columns: new[] { "workspace_id", "active" });

            migrationBuilder.CreateIndex(
                name: "ix_set_links_link_set_id_sort_order",
                table: "SET_LINKS",
                columns: new[] { "link_set_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_set_links_workspace_id",
                table: "SET_LINKS",
                column: "workspace_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""UPDATE "WORKSPACES" SET anti_ban = anti_ban - 'Advanced';""");

            migrationBuilder.DropTable(
                name: "COLLECTION_POSTS");

            migrationBuilder.DropTable(
                name: "REPORT_SHARES");

            migrationBuilder.DropTable(
                name: "SCHEDULES");

            migrationBuilder.DropTable(
                name: "SET_LINKS");

            migrationBuilder.DropTable(
                name: "POST_COLLECTIONS");

            migrationBuilder.DropTable(
                name: "LINK_SETS");

            migrationBuilder.DropIndex(
                name: "ix_posts_link_id_scheduled_at",
                table: "POSTS");

            migrationBuilder.DropIndex(
                name: "ix_posts_schedule_id_status_scheduled_at",
                table: "POSTS");

            migrationBuilder.DropIndex(
                name: "ix_posts_schedule_id_target_key_slot_key",
                table: "POSTS");

            migrationBuilder.DropColumn(
                name: "auto_reply",
                table: "WORKSPACES");

            migrationBuilder.DropColumn(
                name: "notifications",
                table: "WORKSPACES");

            migrationBuilder.DropColumn(
                name: "code",
                table: "POSTS");

            migrationBuilder.DropColumn(
                name: "collection_post_id",
                table: "POSTS");

            migrationBuilder.DropColumn(
                name: "is_test",
                table: "POSTS");

            migrationBuilder.DropColumn(
                name: "link_id",
                table: "POSTS");

            migrationBuilder.DropColumn(
                name: "schedule_id",
                table: "POSTS");

            migrationBuilder.DropColumn(
                name: "slot_key",
                table: "POSTS");

            migrationBuilder.DropColumn(
                name: "target_key",
                table: "POSTS");

            migrationBuilder.DropColumn(
                name: "target_url",
                table: "POSTS");

            migrationBuilder.DropColumn(
                name: "auto_pause_reason",
                table: "DEVICES");

            migrationBuilder.DropColumn(
                name: "auto_paused_until",
                table: "DEVICES");
        }
    }
}
