using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace SIRIAUTOPOST.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class TeamAndBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cycle",
                table: "users",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Month"); // existing customers

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_seen_at",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "limit_overrides",
                table: "users",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "note",
                table: "users",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "paused",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "users",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Active"); // existing customers

            migrationBuilder.CreateTable(
                name: "plan_settings",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    price = table.Column<int>(type: "integer", nullable: false),
                    accounts = table.Column<int>(type: "integer", nullable: true),
                    posts = table.Column<int>(type: "integer", nullable: true),
                    devices = table.Column<int>(type: "integer", nullable: true),
                    seats = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_plan_settings", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "promos",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    discount = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    uses = table.Column<int>(type: "integer", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_promos", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    amount = table.Column<int>(type: "integer", nullable: false),
                    plan = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    cycle = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    promo_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    refund_of_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_transactions", x => x.id);
                    table.ForeignKey(
                        name: "fk_transactions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workspace_members",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    role = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    invited_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workspace_members", x => x.id);
                    table.ForeignKey(
                        name: "fk_workspace_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_workspace_members_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "plan_settings",
                columns: new[] { "key", "accounts", "devices", "posts", "price", "seats" },
                values: new object[,]
                {
                    { "Agency", null, null, null, 1990, 10 },
                    { "Basic", 2, 1, 30, 290, 1 },
                    { "Free", 1, 1, 10, 0, 1 },
                    { "Pro", 10, 3, null, 790, 1 }
                });

            migrationBuilder.CreateIndex(
                name: "ix_promos_code",
                table: "promos",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_transactions_created_at",
                table: "transactions",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_transactions_user_id_created_at",
                table: "transactions",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_workspace_members_user_id",
                table: "workspace_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_workspace_members_workspace_id_email",
                table: "workspace_members",
                columns: new[] { "workspace_id", "email" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "plan_settings");

            migrationBuilder.DropTable(
                name: "promos");

            migrationBuilder.DropTable(
                name: "transactions");

            migrationBuilder.DropTable(
                name: "workspace_members");

            migrationBuilder.DropColumn(
                name: "cycle",
                table: "users");

            migrationBuilder.DropColumn(
                name: "last_seen_at",
                table: "users");

            migrationBuilder.DropColumn(
                name: "limit_overrides",
                table: "users");

            migrationBuilder.DropColumn(
                name: "note",
                table: "users");

            migrationBuilder.DropColumn(
                name: "paused",
                table: "users");

            migrationBuilder.DropColumn(
                name: "status",
                table: "users");
        }
    }
}
