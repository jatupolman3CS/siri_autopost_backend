using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SIRIAUTOPOST.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class ExtensionSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "device_commands",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cmd = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    args = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    result = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    done_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_commands", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_commands_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "device_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    t = table.Column<long>(type: "bigint", nullable: false),
                    level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_logs", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_logs_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "device_states",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    json = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_states", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_states_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "extension_configs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    settings = table.Column<string>(type: "text", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    has_content = table.Column<bool>(type: "boolean", nullable: false),
                    updated_by_device = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_extension_configs", x => x.id);
                    table.ForeignKey(
                        name: "fk_extension_configs_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_extension_configs_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "extension_images",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    image_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    size = table.Column<long>(type: "bigint", nullable: false),
                    data = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_extension_images", x => x.id);
                    table.ForeignKey(
                        name: "fk_extension_images_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_device_commands_device_id_status",
                table: "device_commands",
                columns: new[] { "device_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_device_logs_device_id_t",
                table: "device_logs",
                columns: new[] { "device_id", "t" });

            migrationBuilder.CreateIndex(
                name: "ix_device_states_device_id",
                table: "device_states",
                column: "device_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_extension_configs_device_id",
                table: "extension_configs",
                column: "device_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_extension_configs_workspace_id",
                table: "extension_configs",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ix_extension_images_workspace_id_image_id",
                table: "extension_images",
                columns: new[] { "workspace_id", "image_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "device_commands");

            migrationBuilder.DropTable(
                name: "device_logs");

            migrationBuilder.DropTable(
                name: "device_states");

            migrationBuilder.DropTable(
                name: "extension_configs");

            migrationBuilder.DropTable(
                name: "extension_images");
        }
    }
}
