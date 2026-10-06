using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIRIAUTOPOST.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class FacebookOnlyBumpPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ---- data first ----
            // 1. Extensions of one workspace now have different names: a repeated name (the browsers paired with the
            //    same default "Chrome <date>") gets " (2)", " (3)"... in the order they were paired. The account that
            //    follows its browser's name is renamed with it.
            migrationBuilder.Sql(@"
                WITH ranked AS (
                    SELECT id, name, row_number() OVER (PARTITION BY workspace_id, name ORDER BY created_at, id) AS n
                    FROM ""DEVICES"")
                UPDATE ""DEVICES"" d
                SET name = left(r.name, 80 - length(' (' || r.n || ')')) || ' (' || r.n || ')'
                FROM ranked r
                WHERE d.id = r.id AND r.n > 1;");
            migrationBuilder.Sql(@"
                UPDATE ""SOCIAL_ACCOUNTS"" a
                SET name = 'Facebook · ' || d.name
                FROM ""DEVICES"" d
                WHERE a.device_id = d.id AND a.name <> 'Facebook · ' || d.name;");

            // 2. Facebook is the only platform now, and a workspace starts empty: the sample accounts of the design
            //    (Instagram, X, TikTok, LINE, Threads and the sample Facebook page and profile, which no browser ever
            //    posted for) go, with their posts (the foreign key cascades). A connected account, or one whose
            //    browser was unbound ('Facebook · <browser>'), is real and stays.
            migrationBuilder.Sql(@"
                DELETE FROM ""SOCIAL_ACCOUNTS""
                WHERE ""platform"" <> 'Fb'
                   OR (device_id IS NULL AND name NOT LIKE 'Facebook · %');");
            migrationBuilder.Sql(@"
                UPDATE ""LINK_SETS"" s
                SET account_ids = COALESCE((SELECT array_agg(x) FROM unnest(s.account_ids) x WHERE x IN (SELECT id FROM ""SOCIAL_ACCOUNTS"")), '{}'),
                    post_as_account_id = CASE WHEN s.post_as_account_id IN (SELECT id FROM ""SOCIAL_ACCOUNTS"") THEN s.post_as_account_id END;");

            migrationBuilder.DropIndex(
                name: "ix_devices_workspace_id",
                table: "DEVICES");

            migrationBuilder.AddColumn<string>(
                name: "bump_plan",
                table: "SCHEDULES",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "post_url",
                table: "POSTS",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "groups",
                table: "PLAN_SETTINGS",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "images",
                table: "PLAN_SETTINGS",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "library_posts",
                table: "PLAN_SETTINGS",
                type: "integer",
                nullable: true);


            migrationBuilder.CreateTable(
                name: "POST_BUMPS",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    schedule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    target = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    text = table.Column<string>(type: "character varying(1200)", maxLength: 1200, nullable: false),
                    media_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                    round = table.Column<int>(type: "integer", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    claimed_by_device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    done_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_post_bumps", x => x.id);
                    table.ForeignKey(
                        name: "fk_post_bumps_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "SOCIAL_ACCOUNTS",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_post_bumps_devices_claimed_by_device_id",
                        column: x => x.claimed_by_device_id,
                        principalTable: "DEVICES",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_post_bumps_posts_post_id",
                        column: x => x.post_id,
                        principalTable: "POSTS",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_post_bumps_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "WORKSPACES",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "PLAN_SETTINGS",
                keyColumn: "key",
                keyValue: "Agency",
                columns: new[] { "groups", "images", "library_posts" },
                values: new object[] { null, null, null });

            migrationBuilder.UpdateData(
                table: "PLAN_SETTINGS",
                keyColumn: "key",
                keyValue: "Basic",
                columns: new[] { "groups", "images", "library_posts", "posts" },
                values: new object[] { 50, 200, 200, 50 });

            migrationBuilder.UpdateData(
                table: "PLAN_SETTINGS",
                keyColumn: "key",
                keyValue: "Free",
                columns: new[] { "groups", "images", "library_posts" },
                values: new object[] { 10, 20, 20 });

            migrationBuilder.UpdateData(
                table: "PLAN_SETTINGS",
                keyColumn: "key",
                keyValue: "Pro",
                columns: new[] { "groups", "images", "library_posts", "posts", "seats" },
                values: new object[] { 300, 1000, 1000, 300, 3 });

            migrationBuilder.CreateIndex(
                name: "ix_devices_workspace_id_name",
                table: "DEVICES",
                columns: new[] { "workspace_id", "name" },
                unique: true);



            migrationBuilder.CreateIndex(
                name: "ix_post_bumps_account_id_status_due_at",
                table: "POST_BUMPS",
                columns: new[] { "account_id", "status", "due_at" });

            migrationBuilder.CreateIndex(
                name: "ix_post_bumps_claimed_by_device_id",
                table: "POST_BUMPS",
                column: "claimed_by_device_id");

            migrationBuilder.CreateIndex(
                name: "ix_post_bumps_post_id_round",
                table: "POST_BUMPS",
                columns: new[] { "post_id", "round" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_post_bumps_schedule_id",
                table: "POST_BUMPS",
                column: "schedule_id",
                filter: "schedule_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_post_bumps_workspace_id",
                table: "POST_BUMPS",
                column: "workspace_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "POST_BUMPS");

            migrationBuilder.DropIndex(
                name: "ix_devices_workspace_id_name",
                table: "DEVICES");

            migrationBuilder.DropColumn(
                name: "bump_plan",
                table: "SCHEDULES");

            migrationBuilder.DropColumn(
                name: "post_url",
                table: "POSTS");

            migrationBuilder.DropColumn(
                name: "groups",
                table: "PLAN_SETTINGS");

            migrationBuilder.DropColumn(
                name: "images",
                table: "PLAN_SETTINGS");

            migrationBuilder.DropColumn(
                name: "library_posts",
                table: "PLAN_SETTINGS");

            migrationBuilder.UpdateData(
                table: "PLAN_SETTINGS",
                keyColumn: "key",
                keyValue: "Basic",
                column: "posts",
                value: 30);

            migrationBuilder.UpdateData(
                table: "PLAN_SETTINGS",
                keyColumn: "key",
                keyValue: "Pro",
                columns: new[] { "posts", "seats" },
                values: new object[] { null, 1 });

            migrationBuilder.CreateIndex(
                name: "ix_devices_workspace_id",
                table: "DEVICES",
                column: "workspace_id");
        }
    }
}
